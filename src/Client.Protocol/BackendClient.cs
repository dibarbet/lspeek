using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ManualLspClient.Protocol;

/// <summary>Options controlling how <see cref="BackendClient"/> spawns and talks to a backend.</summary>
public sealed class BackendClientOptions
{
    /// <summary>Instance id to address; each frontend defaults to a single private instance.</summary>
    public string Instance { get; set; } = "default";

    /// <summary>Explicit backend executable or directory; falls back to <see cref="BackendLauncher"/> discovery.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Port to bind; 0 lets the OS pick an ephemeral port (recommended).</summary>
    public int Port { get; set; }

    /// <summary>Pass <c>--watch-stdin</c> so the backend stops when this client closes its stdin pipe.</summary>
    public bool WatchStdin { get; set; } = true;

    /// <summary>Pass <c>--parent-pid</c> so the backend stops if this process dies unexpectedly.</summary>
    public bool PassParentPid { get; set; } = true;

    /// <summary>Extra arguments appended to the backend command line.</summary>
    public IReadOnlyList<string> ExtraArguments { get; set; } = Array.Empty<string>();

    /// <summary>How long to wait for the readiness handshake before failing.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional sink for non-handshake backend stdout lines.</summary>
    public Action<string>? OnStdout { get; set; }

    /// <summary>Optional sink for backend stderr lines.</summary>
    public Action<string>? OnStderr { get; set; }
}

/// <summary>A parsed Server-Sent Event from the backend's <c>/events</c> stream.</summary>
public sealed record BackendEvent(string Event, string Data)
{
    /// <summary>Deserializes the payload as a message record (for <c>message</c> events).</summary>
    public LspMessageRecord? AsMessage()
        => Event == "message" ? BackendJson.Deserialize<LspMessageRecord>(Data) : null;

    /// <summary>Deserializes the payload as a status snapshot (for <c>status</c> events).</summary>
    public ServerStatus? AsStatus()
        => Event == "status" ? BackendJson.Deserialize<ServerStatus>(Data) : null;
}

/// <summary>
/// Spawns and drives a private <c>lspeek-backend</c> process over its loopback HTTP+SSE API.
/// One typed method per backend endpoint, plus <see cref="StreamEventsAsync"/> for live
/// status/message events. Shared by the TUI and MCP frontends.
/// </summary>
public sealed class BackendClient : IAsyncDisposable
{
    private const string HandshakePrefix = "LSPEEK_BACKEND_URL=";

    private readonly BackendClientOptions _options;
    private readonly string _instanceQuery;

    private Process? _process;
    private HttpClient? _http;
    private string? _baseUrl;
    private bool _disposed;

    public BackendClient(BackendClientOptions? options = null)
    {
        _options = options ?? new BackendClientOptions();
        _instanceQuery = "instance=" + Uri.EscapeDataString(_options.Instance);
    }

    /// <summary>The backend's base URL once <see cref="StartAsync"/> has completed.</summary>
    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("Backend not started.");

    /// <summary>The spawned backend's process id, if running.</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    /// <summary>Raised when the backend process exits.</summary>
    public event Action<int>? ProcessExited;

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Spawns the backend and waits for its readiness handshake.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
            throw new InvalidOperationException("Backend already started.");

        var (fileName, prefixArgs) = BackendLauncher.Resolve(_options.ExecutablePath);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefixArgs)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_options.Port.ToString(CultureInfo.InvariantCulture));
        if (_options.WatchStdin)
            psi.ArgumentList.Add("--watch-stdin");
        if (_options.PassParentPid)
        {
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var a in _options.ExtraArguments)
            psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            if (e.Data.StartsWith(HandshakePrefix, StringComparison.Ordinal))
                ready.TrySetResult(e.Data[HandshakePrefix.Length..].Trim());
            else
                _options.OnStdout?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
                stderr.AppendLine(e.Data);
            _options.OnStderr?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            string captured;
            lock (stderr)
                captured = stderr.ToString();
            ready.TrySetException(new BackendException(
                $"Backend exited (code {SafeExitCode(process)}) before signaling readiness.\n{captured}"));
            ProcessExited?.Invoke(SafeExitCode(process) ?? -1);
        };

        if (!process.Start())
            throw new BackendException("Failed to start the backend process.");
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);

        string url;
        try
        {
            url = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string captured;
            lock (stderr)
                captured = stderr.ToString();
            throw new BackendException(
                $"Backend did not signal readiness within {_options.StartupTimeout.TotalSeconds:0}s.\n{captured}");
        }

        _baseUrl = url.TrimEnd('/');
        _http = new HttpClient();
    }

    // ── server lifecycle actions ───────────────────────────────────────────────

    public Task<StartServerResponse> StartServerAsync(StartServerRequest request, CancellationToken ct = default)
        => PostAsync<StartServerResponse>("/api/start", request, ct);

    public Task StopServerAsync(CancellationToken ct = default)
        => PostVoidAsync("/api/stop", null, ct);

    public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
        => GetAsync<ServerStatus>("/api/status", ct);

    public Task<InstanceState> GetStateAsync(CancellationToken ct = default)
        => GetAsync<InstanceState>("/api/state", ct);

    public async Task<string> GetHelpAsync(CancellationToken ct = default)
        => (await GetAsync<HelpResponse>("/api/help", ct).ConfigureAwait(false)).Help;

    // ── LSP traffic actions ────────────────────────────────────────────────────

    public Task<LspRequestResult> RequestAsync(LspRequestInput input, CancellationToken ct = default)
        => PostAsync<LspRequestResult>("/api/request", input, ct);

    public Task<LspRequestResult> RequestAsync(string method, JsonElement? @params = null, int? timeoutMs = null, CancellationToken ct = default)
        => RequestAsync(new LspRequestInput { Method = method, Params = @params, TimeoutMs = timeoutMs }, ct);

    public Task NotifyAsync(LspNotifyInput input, CancellationToken ct = default)
        => PostVoidAsync("/api/notify", input, ct);

    public Task NotifyAsync(string method, JsonElement? @params = null, CancellationToken ct = default)
        => NotifyAsync(new LspNotifyInput { Method = method, Params = @params }, ct);

    public Task<SendRawResult> SendRawAsync(SendRawInput input, CancellationToken ct = default)
        => PostAsync<SendRawResult>("/api/send-raw", input, ct);

    public Task RespondAsync(RespondInput input, CancellationToken ct = default)
        => PostVoidAsync("/api/respond", input, ct);

    // ── message buffer actions ─────────────────────────────────────────────────

    public Task<GetMessagesResult> GetMessagesAsync(GetMessagesQuery query, CancellationToken ct = default)
        => GetAsync<GetMessagesResult>("/api/messages" + BuildMessagesQuery(query), ct);

    public Task<WaitForMessageResult> WaitForMessageAsync(WaitForMessageInput input, CancellationToken ct = default)
        => PostAsync<WaitForMessageResult>("/api/wait", input, ct);

    public Task ClearMessagesAsync(CancellationToken ct = default)
        => PostVoidAsync("/api/clear", null, ct);

    // ── live events ────────────────────────────────────────────────────────────

    /// <summary>Streams parsed Server-Sent Events (status/message/cleared) until cancelled or disconnected.</summary>
    public async IAsyncEnumerable<BackendEvent> StreamEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var http = Http();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/events"));
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break; // stream closed

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new BackendEvent(eventName ?? "message", data.ToString());
                    eventName = null;
                    data.Clear();
                }
                continue;
            }

            if (line[0] == ':')
                continue; // comment / heartbeat

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
            // other fields (retry:, id:) are ignored
        }
    }

    // ── plumbing ───────────────────────────────────────────────────────────────

    private HttpClient Http() => _http ?? throw new InvalidOperationException("Backend not started.");

    private string Url(string path)
    {
        var separator = path.Contains('?') ? '&' : '?';
        return $"{BaseUrl}{path}{separator}{_instanceQuery}";
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await Http().GetAsync(Url(path), ct).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
    {
        using var content = CreateContent(body);
        using var response = await Http().PostAsync(Url(path), content, ct).ConfigureAwait(false);
        return await ReadResultAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task PostVoidAsync(string path, object? body, CancellationToken ct)
    {
        using var content = CreateContent(body);
        using var response = await Http().PostAsync(Url(path), content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private static HttpContent? CreateContent(object? body)
        => body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body, BackendJson.Options), Encoding.UTF8, "application/json");

    private static async Task<T> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw ToBackendException(response, json);

        var result = JsonSerializer.Deserialize<T>(json, BackendJson.Options);
        return result ?? throw new BackendException(
            $"Backend returned an empty or invalid body where {typeof(T).Name} was expected.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw ToBackendException(response, json);
    }

    private static BackendException ToBackendException(HttpResponseMessage response, string json)
    {
        string? message = null;
        try
        {
            message = BackendJson.Deserialize<ErrorResponse>(json)?.Error;
        }
        catch (JsonException)
        {
            // body was not an error envelope
        }
        return new BackendException(message ?? $"Backend returned HTTP {(int)response.StatusCode}: {json}");
    }

    private static string BuildMessagesQuery(GetMessagesQuery q)
    {
        var parts = new List<string>
        {
            $"sinceSeq={q.SinceSeq}",
            $"limit={q.Limit}",
            $"includePayload={(q.IncludePayload ? "true" : "false")}",
        };
        if (!string.IsNullOrEmpty(q.Direction))
            parts.Add("direction=" + Uri.EscapeDataString(q.Direction));
        if (!string.IsNullOrEmpty(q.MethodContains))
            parts.Add("methodContains=" + Uri.EscapeDataString(q.MethodContains));
        if (q.Kinds is { Count: > 0 })
            parts.Add("kinds=" + Uri.EscapeDataString(string.Join(",", q.Kinds)));
        return "?" + string.Join("&", parts);
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _http?.Dispose();

        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    // Closing stdin triggers the backend's --watch-stdin graceful shutdown,
                    // which also stops the LSP server child.
                    process.StandardInput.Close();
                    await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(2000)).ConfigureAwait(false);
                }
            }
            catch
            {
                // best effort
            }

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            process.Dispose();
        }
    }
}
