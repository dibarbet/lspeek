using System.Text.Json;
using System.Text.Json.Nodes;
using Lspeek.Core.Configuration;
using Lspeek.Core.Session;
using Lspeek.Core.Transport;
using Lspeek.Protocol;

namespace Lspeek.Backend.Hosting;

/// <summary>
/// Owns one LSP server's lifecycle for a single <c>instanceId</c>: resolves + spawns the
/// process, wires a <see cref="RawLspConnection"/> and a <see cref="MessageBuffer"/>, and
/// exposes the unified action surface (start/stop/status/request/notify/send-raw/respond/
/// messages/wait/clear). Ported from the canvas extension's <c>LspInstance</c>.
/// </summary>
public sealed class BackendInstance : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private LspServerProcess? _process;
    private RawLspConnection? _connection;
    private string _status = "stopped";
    private JsonElement? _exitInfo;
    private string? _resolvedDll;
    private IReadOnlyList<string>? _commandLine;
    private DateTimeOffset? _startedAt;
    private bool _stopping;

    public BackendInstance(string instanceId)
    {
        InstanceId = instanceId;
    }

    public string InstanceId { get; }

    /// <summary>The seq-numbered traffic buffer (subscribe to its events for SSE fan-out).</summary>
    public MessageBuffer Buffer { get; } = new();

    /// <summary>Raised whenever the server status changes (started/stopped/exited).</summary>
    public event Action<ServerStatus>? StatusChanged;

    // ── lifecycle ────────────────────────────────────────────────────────────

    public async Task<StartServerResponse> StartAsync(StartServerRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null && _status is "running" or "starting")
                await StopCoreAsync().ConfigureAwait(false);

            ServerConfig config;
            IReadOnlyList<ServerCandidate> candidates;
            string? resolvedDll;

            if (RoslynServerResolver.IsRoslynRequest(request))
            {
                config = RoslynServerResolver.BuildConfig(request, out candidates);
                resolvedDll = config.Arguments.FirstOrDefault();
            }
            else if (!string.IsNullOrWhiteSpace(request.Server))
            {
                var provider = ServerConfigProvider.Load();
                config = provider.Resolve(request.Server!);
                if (!string.IsNullOrWhiteSpace(request.Cwd))
                    config.WorkingDirectory = request.Cwd;
                if (request.Env is { Count: > 0 })
                {
                    config.Environment ??= new Dictionary<string, string>();
                    foreach (var (k, v) in request.Env)
                        config.Environment[k] = v;
                }
                if (request.ExtraArgs is { Count: > 0 })
                    config.Arguments = [.. config.Arguments, .. request.ExtraArgs];
                candidates = [];
                resolvedDll = null;
            }
            else
            {
                throw new ArgumentException(
                    "Provide either 'server' (a built-in name or config path) or 'serverPath'/'repoRoot' (a Roslyn build).");
            }

            var autoRespond = request.AutoRespond != false;

            lock (_sync)
            {
                _status = "starting";
                _exitInfo = null;
                _stopping = false;
            }

            var process = LspServerProcess.Start(config);
            var connection = new RawLspConnection(process.InputStream, process.OutputStream, autoRespond);

            connection.MessageObserved += OnObserved;
            process.StderrLineReceived += OnStderr;
            process.Exited += OnProcessExited;

            connection.Start();

            var commandLine = new List<string> { config.Command };
            commandLine.AddRange(config.Arguments);

            lock (_sync)
            {
                _process = process;
                _connection = connection;
                _resolvedDll = resolvedDll;
                _commandLine = commandLine;
                _startedAt = DateTimeOffset.UtcNow;
                _status = process.HasExited ? "exited" : "running";
            }

            Buffer.Add("meta", "info", null, null,
                $"Started server (pid {process.ProcessId}): {string.Join(" ", commandLine)}",
                ToElement(new JsonObject
                {
                    ["pid"] = process.ProcessId,
                    ["commandLine"] = new JsonArray([.. commandLine.Select(a => (JsonNode)a!)]),
                    ["dllPath"] = resolvedDll,
                }));

            RaiseStatus();

            return new StartServerResponse
            {
                Pid = process.ProcessId,
                DllPath = resolvedDll,
                CommandLine = commandLine,
                Candidates = candidates,
                Next = "Send 'initialize' via lsp_request, then 'initialized' via lsp_notify.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(bool graceful = true, int timeoutMs = 3000)
    {
        LspServerProcess? process;
        RawLspConnection? connection;
        lock (_sync)
        {
            process = _process;
            connection = _connection;
            if (process is null)
                return;
            _stopping = true;
        }

        if (graceful && connection is not null && !process.HasExited)
        {
            try
            {
                // `shutdown` takes no params — never put `params:null` on the wire.
                await connection.SendRequestAsync("shutdown", null, 1500).ConfigureAwait(false);
            }
            catch
            {
                // ignore — we kill below regardless.
            }
            try
            {
                connection.SendNotification("exit", null);
            }
            catch
            {
                // ignore
            }

            try
            {
                using var grace = new CancellationTokenSource(timeoutMs);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch
            {
                // timed out / cancelled — DisposeAsync kills the tree below.
            }
        }

        var exitInfo = process.HasExited ? ToElement(new JsonObject { ["code"] = process.ExitCode }) : (JsonElement?)null;

        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        await process.DisposeAsync().ConfigureAwait(false);

        lock (_sync)
        {
            _process = null;
            _connection = null;
            _status = "stopped";
            _exitInfo = exitInfo;
            _startedAt = null;
        }

        Buffer.FailWaiters(new InvalidOperationException("Server stopped."));
        Buffer.Add("meta", "info", null, null, "Stopped server.",
            exitInfo is { } e ? ToElement(new JsonObject { ["exitInfo"] = JsonNode.Parse(e.GetRawText()) }) : (JsonElement?)null);
        RaiseStatus();
    }

    // ── actions ────────────────────────────────────────────────────────────

    public async Task<LspRequestResult> RequestAsync(LspRequestInput input)
    {
        var connection = RequireConnection();
        var response = await connection
            .SendRequestAsync(input.Method, ToNode(input.Params), input.TimeoutMs ?? 30000)
            .ConfigureAwait(false);
        return new LspRequestResult
        {
            Id = GetProperty(response, "id"),
            Result = GetProperty(response, "result"),
            Error = GetProperty(response, "error"),
        };
    }

    public void Notify(LspNotifyInput input)
        => RequireConnection().SendNotification(input.Method, ToNode(input.Params));

    public async Task<SendRawResult> SendRawAsync(SendRawInput input)
    {
        var connection = RequireConnection();
        var node = ToNodeRequired(input.Message);
        var response = await connection
            .SendRawAsync(node, input.WaitForResponse, input.TimeoutMs ?? 30000)
            .ConfigureAwait(false);
        return new SendRawResult { Response = response };
    }

    public void Respond(RespondInput input)
        => RequireConnection().Respond(ToNodeRequired(input.Id), ToNode(input.Result), ToNode(input.Error));

    public GetMessagesResult GetMessages(GetMessagesQuery query) => Buffer.GetMessages(query);

    public async Task<WaitForMessageResult> WaitAsync(WaitForMessageInput input)
    {
        try
        {
            var matched = await Buffer.WaitForMessageAsync(input).ConfigureAwait(false);
            return new WaitForMessageResult { Matched = matched };
        }
        catch (Exception ex)
        {
            var recent = Buffer
                .GetMessages(new GetMessagesQuery { SinceSeq = 0, Limit = 15, IncludePayload = false })
                .Messages;
            return new WaitForMessageResult { TimedOut = true, Error = ex.Message, Recent = recent };
        }
    }

    public void Clear() => Buffer.Clear();

    public ServerStatus SnapshotStatus()
    {
        lock (_sync)
        {
            return new ServerStatus
            {
                InstanceId = InstanceId,
                Status = _status,
                Pid = _process?.ProcessId,
                DllPath = _resolvedDll,
                CommandLine = _commandLine,
                UptimeMs = _startedAt is { } s && _status == "running"
                    ? (long)(DateTimeOffset.UtcNow - s).TotalMilliseconds
                    : null,
                MessageCount = Buffer.Count,
                PendingRequests = _connection?.PendingRequestCount ?? 0,
                ExitInfo = _exitInfo,
            };
        }
    }

    // ── event handlers ───────────────────────────────────────────────────────

    private void OnObserved(ObservedMessage m)
        => Buffer.Add(m.Direction, m.Kind, m.Method, m.Id, m.Summary, m.Payload);

    private void OnStderr(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        var summary = line.Length > 200 ? line[..200] + "…" : line;
        Buffer.Add("recv", "stderr", null, null, summary, JsonSerializer.SerializeToElement(line, BackendJsonContext.Default.String));
    }

    private void OnProcessExited()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_stopping || _process is null)
                return;
            var wasRunning = _status is "running";
            _status = wasRunning ? "exited" : "error";
            _exitInfo = ToElement(new JsonObject { ["code"] = _process.ExitCode });
            changed = true;
        }
        if (changed)
        {
            Buffer.FailWaiters(new InvalidOperationException("Server exited."));
            RaiseStatus();
        }
    }

    private RawLspConnection RequireConnection()
    {
        lock (_sync)
        {
            if (_connection is null || _status != "running")
                throw new ServerNotRunningException();
            return _connection;
        }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(SnapshotStatus());

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static JsonNode? ToNode(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return JsonNode.Parse(e.GetRawText());
    }

    private static JsonNode ToNodeRequired(JsonElement element)
        => JsonNode.Parse(element.GetRawText())
           ?? throw new ArgumentException("Expected a JSON value.");

    private static JsonElement? GetProperty(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? v.Clone()
            : null;

    private static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
        _gate.Dispose();
    }
}
