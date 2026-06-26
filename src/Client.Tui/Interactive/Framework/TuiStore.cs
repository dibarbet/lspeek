using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Protocol;
using ManualLspClient.Tui.Scripting;
using System.Text.Json;

namespace ManualLspClient.Tui.Interactive.Framework;

/// <summary>
/// Shared state container for the TUI. Drives a private <see cref="BackendClient"/> (which owns
/// the LSP server) and rebuilds a local <see cref="SessionLog"/> from the backend's live SSE
/// stream, so the existing Spectre views, progress tracker, and exporters work unchanged.
/// Views read from this store and invoke actions through it.
/// </summary>
public class TuiStore
{
    private readonly BackendClient _client;
    private readonly SessionLog _log = new();
    private readonly WorkDoneProgressTracker _progress = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly object _sync = new();
    private readonly List<LspMessageRecord> _pending = [];
    private readonly TaskCompletionSource _sseConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _maxSeq;
    private bool _backfilled;

    private volatile bool _running;
    private volatile bool _initialized;
    private int _pid;
    private int _lastObservedMessageCount;
    private Task? _sseLoop;

    public LspMetaModelProvider MetaModel { get; }

    /// <summary>
    /// Index of the currently selected message in the collapsed log.
    /// Persisted across view transitions so the list remembers position.
    /// </summary>
    public int SelectedIndex { get; set; } = -1;

    public bool AutoInit { get; set; }

    public string ServerName { get; }

    public TuiStore(BackendClient client, LspMetaModelProvider metaModel, string serverName, ServerStatus? initialStatus = null)
    {
        _client = client;
        MetaModel = metaModel;
        ServerName = serverName;

        // Wire local session log → progress tracker.
        _log.MessageAdded += _progress.OnMessageAdded;

        if (initialStatus is not null)
            ApplyStatus(initialStatus);
    }

    /// <summary>
    /// Connects to the backend's SSE stream and backfills any messages already buffered. After
    /// this completes, live traffic flows into the local log automatically.
    /// </summary>
    public async Task StartAsync()
    {
        _sseLoop = Task.Run(() => RunSseLoopAsync(_cts.Token));
        await BackfillAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the initialize request and initialized notification through the backend.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _client.RequestAsync("initialize", BuildDefaultInitializeParams(), timeoutMs: 30000, ct: _cts.Token)
                .ConfigureAwait(false);
            await _client.NotifyAsync("initialized", EmptyObject(), _cts.Token).ConfigureAwait(false);
            _initialized = true;
        }
        catch (Exception)
        {
            // Initialize failures are visible in the session log.
        }
    }

    // ── Session data accessors ──

    public IReadOnlyList<SessionMessage> GetMessages() => _log.GetAll();
    public int MessageCount => _log.Count;
    public bool IsServerRunning => _running;
    public bool IsInitialized => _initialized;
    public int ServerProcessId { get { lock (_sync) return _pid; } }
    public CancellationToken CancellationToken => _cts.Token;

    // ── Progress tracking ──

    public IReadOnlyList<WorkDoneProgressItem> GetProgressItems() => _progress.GetVisibleItems();
    public bool HasActiveProgress => _progress.HasVisibleItems;

    // ── Actions ──

    /// <summary>
    /// Send an LSP request or notification. Parses the JSON and dispatches via the backend.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public async Task<string?> DispatchMessageAsync(string method, string? paramsJson, bool isNotification)
    {
        JsonElement? paramsElement = null;
        if (paramsJson is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(paramsJson);
                paramsElement = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return $"Invalid JSON: {ex.Message}";
            }
        }

        try
        {
            if (isNotification)
            {
                await _client.NotifyAsync(method, paramsElement, _cts.Token).ConfigureAwait(false);
                if (method.Equals("initialized", StringComparison.OrdinalIgnoreCase))
                    _initialized = true;
            }
            else
            {
                await _client.RequestAsync(method, paramsElement, timeoutMs: null, ct: _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>
    /// Exports sent messages as a replayable JSON script.
    /// </summary>
    public void ExportSession(string path)
    {
        ScriptExporter.Export(_log, path);
    }

    /// <summary>
    /// Exports the full session log (all messages) as a JSON file.
    /// </summary>
    public void ExportFullSessionLog(string path)
    {
        SessionLogExporter.Export(_log, path);
    }

    /// <summary>
    /// Sends shutdown/exit (via the backend) and cancels the main loop.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            await _client.StopServerAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort — the backend or server may have already exited.
        }
        finally
        {
            _cts.Cancel();
        }
    }

    /// <summary>
    /// Copies text to the clipboard.
    /// </summary>
    public static void CopyToClipboard(string text)
    {
        TextCopy.ClipboardService.SetText(text);
    }

    // ── SSE ingestion ──

    private async Task RunSseLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _client.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                // The backend emits an initial status snapshot immediately after subscribing to
                // the buffer; receiving any event means subsequent records will reach us live.
                _sseConnected.TrySetResult();

                switch (evt.Event)
                {
                    case "message" when evt.AsMessage() is { } record:
                        OnRecord(record);
                        break;
                    case "status" when evt.AsStatus() is { } status:
                        ApplyStatus(status);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception)
        {
            // Stream ended (backend gone); the header will reflect the stopped state.
        }
    }

    private async Task BackfillAsync()
    {
        try
        {
            // Wait briefly for the SSE subscription so records added during startup land in
            // _pending and aren't missed between the snapshot and the live stream.
            await _sseConnected.Task.WaitAsync(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Proceed without the signal; the snapshot below is still applied.
        }

        InstanceState? state = null;
        try
        {
            state = await _client.GetStateAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Backend unreachable; nothing to backfill.
        }

        lock (_sync)
        {
            if (state is not null)
            {
                foreach (var record in state.Messages)
                    ApplyRecordLocked(record);
                ApplyStatus(state.Status);
            }

            foreach (var record in _pending.OrderBy(r => r.Seq))
                ApplyRecordLocked(record);
            _pending.Clear();
            _backfilled = true;
        }
    }

    private void OnRecord(LspMessageRecord record)
    {
        lock (_sync)
        {
            if (!_backfilled)
            {
                _pending.Add(record);
                return;
            }
            ApplyRecordLocked(record);
        }
    }

    private void ApplyRecordLocked(LspMessageRecord record)
    {
        if (record.Seq <= _maxSeq)
            return;
        _maxSeq = record.Seq;

        if (BackendMessageMapper.IsStderr(record))
            _log.AddStderrLine(BackendMessageMapper.GetStderrText(record));
        else
            _log.Add(BackendMessageMapper.ToSessionMessage(record));
    }

    private void ApplyStatus(ServerStatus status)
    {
        _running = string.Equals(status.Status, "running", StringComparison.Ordinal);
        lock (_sync)
        {
            if (status.Pid is { } pid)
                _pid = pid;
        }
    }

    // ── Initialize params ──

    private static JsonElement BuildDefaultInitializeParams()
    {
        var workspaceUri = new Uri(Environment.CurrentDirectory).AbsoluteUri;
        return JsonSerializer.SerializeToElement(new
        {
            processId = Environment.ProcessId,
            capabilities = new
            {
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = true },
                    completion = new
                    {
                        dynamicRegistration = true,
                        completionItem = new { snippetSupport = true }
                    },
                    hover = new { dynamicRegistration = true },
                    definition = new { dynamicRegistration = true },
                    references = new { dynamicRegistration = true },
                    documentSymbol = new { dynamicRegistration = true },
                    diagnostics = new { dynamicRegistration = true }
                },
                workspace = new
                {
                    workspaceFolders = true,
                    didChangeConfiguration = new { dynamicRegistration = true }
                },
                window = new
                {
                    workDoneProgress = true
                }
            },
            rootUri = workspaceUri,
            workspaceFolders = new[]
            {
                new { uri = workspaceUri, name = Path.GetFileName(Environment.CurrentDirectory) }
            }
        });
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    // ── Helpers ──

    /// <summary>
    /// Finds the index of the matching request (for a response) or response (for a request).
    /// </summary>
    public static int? FindMatchingMessageIndex(SessionMessage msg, IReadOnlyList<SessionMessage> messages)
    {
        if (!msg.Id.HasValue) return null;

        var targetType = msg.MessageType == MessageType.Request
            ? MessageType.Response
            : msg.MessageType == MessageType.Response
                ? MessageType.Request
                : (MessageType?)null;

        if (targetType is null) return null;

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].MessageType == targetType && messages[i].Id == msg.Id)
                return i;
        }

        return null;
    }

    /// <summary>
    /// Keeps the list selection pinned to the newest message only when the
    /// user was already sitting on the previous tail.
    /// </summary>
    public void FollowLatestMessageIfPinned()
    {
        var count = MessageCount;

        if (count == 0)
        {
            SelectedIndex = -1;
            _lastObservedMessageCount = 0;
            return;
        }

        var previousLastIndex = _lastObservedMessageCount - 1;
        var wasPinnedToLatest = SelectedIndex < 0 || SelectedIndex == previousLastIndex;

        if (SelectedIndex >= count || wasPinnedToLatest)
            SelectedIndex = count - 1;

        _lastObservedMessageCount = count;
    }
}
