using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Tui.Scripting;
using Spectre.Console;
using System.Text.Json;

namespace ManualLspClient.Tui.Interactive.Framework;

/// <summary>
/// Shared state container for the TUI. Wraps <see cref="LspSession"/> and exposes
/// session data plus TUI-specific state. Views read from this store and invoke
/// actions through it.
/// </summary>
public class TuiStore
{
    private readonly LspSession _session;
    private readonly CancellationTokenSource _cts = new();

    public LspMetaModelProvider MetaModel { get; }

    /// <summary>
    /// Index of the currently selected message in the collapsed log.
    /// Persisted across view transitions so the list remembers position.
    /// </summary>
    public int SelectedIndex { get; set; } = -1;

    public bool AutoInit { get; set; }

    public TuiStore(LspSession session, LspMetaModelProvider metaModel)
    {
        _session = session;
        MetaModel = metaModel;
    }

    /// <summary>
    /// Sends the initialize request and initialized notification in the background.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _session.InitializeAsync(cancellationToken: _cts.Token);
        }
        catch (Exception)
        {
            // Initialize failures are visible in the session log
        }
    }

    // ── Session data accessors ──

    public IReadOnlyList<SessionMessage> GetMessages() => _session.Log.GetAll();
    public int MessageCount => _session.Log.Count;
    public bool IsServerRunning => _session.IsServerRunning;
    public bool IsInitialized => _session.IsInitialized;
    public int ServerProcessId => _session.ServerProcessId;
    public string ServerName => _session.ServerConfig.Name;
    public CancellationToken CancellationToken => _cts.Token;

    // ── Progress tracking ──

    public IReadOnlyList<WorkDoneProgressItem> GetProgressItems() => _session.ProgressTracker.GetVisibleItems();
    public bool HasActiveProgress => _session.ProgressTracker.HasVisibleItems;

    // ── Actions ──

    /// <summary>
    /// Send an LSP request or notification. Parses the JSON, dispatches, and updates selection.
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

        if (isNotification)
        {
            await _session.SendNotificationAsync(method, paramsElement, _cts.Token);
        }
        else
        {
            await _session.SendRequestAsync(method, paramsElement, _cts.Token);
        }

        SelectedIndex = _session.Log.Count - 1;
        return null;
    }

    /// <summary>
    /// Exports the session log to a JSON file.
    /// </summary>
    public void ExportSession(string path)
    {
        ScriptExporter.Export(_session.Log, path);
    }

    /// <summary>
    /// Sends shutdown/exit and cancels the main loop.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await _session.ShutdownAsync();
        _cts.Cancel();
    }

    /// <summary>
    /// Copies text to the clipboard.
    /// </summary>
    public static void CopyToClipboard(string text)
    {
        TextCopy.ClipboardService.SetText(text);
    }

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
    /// Clamp selected index to valid range given current message count.
    /// </summary>
    public void ClampSelectedIndex()
    {
        var count = MessageCount;
        if (SelectedIndex < 0 || SelectedIndex >= count)
            SelectedIndex = count - 1;
    }
}
