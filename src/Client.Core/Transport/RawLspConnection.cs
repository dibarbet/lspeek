using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lspeek.Core.Transport;

/// <summary>
/// An observed JSON-RPC frame in either direction. Mirrors the canvas client's record shape:
/// direction (send/recv/meta), kind (request/response/notification/error/stderr/info/batch),
/// method, id, a short summary, an optional human-friendly <see cref="Detail"/>, and the full
/// payload.
/// </summary>
public sealed record ObservedMessage(
    string Direction,
    string Kind,
    string? Method,
    JsonElement? Id,
    string Summary,
    JsonElement? Payload,
    string? Detail = null);

/// <summary>
/// Low-level LSP wire client over Content-Length framed JSON-RPC. Mirrors the canvas
/// extension's hand-rolled client for exact parity: composed request/notify,
/// verbatim <c>send_raw</c> (object/array/batch, custom ids), generic handling of any
/// server-&gt;client request, optional auto-respond to infrastructure requests, and manual
/// responses. A single writer is serialized so frames never interleave on the stream.
/// </summary>
public sealed class RawLspConnection : IAsyncDisposable
{
    private static readonly byte[] HeaderSeparator = [13, 10, 13, 10]; // \r\n\r\n

    private readonly Stream _send;     // server stdin (we write)
    private readonly Stream _receive;  // server stdout (we read)
    private readonly bool _autoRespond;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    private int _nextId;
    private Task? _readLoop;
    private bool _disposed;

    private byte[] _acc = new byte[16384];
    private int _accLen;

    /// <summary>Raised for every frame observed in either direction.</summary>
    public event Action<ObservedMessage>? MessageObserved;

    /// <summary>Raised when the read loop ends (server stdout closed / process gone).</summary>
    public event Action<string>? Disconnected;

    public RawLspConnection(Stream send, Stream receive, bool autoRespond = true)
    {
        _send = send;
        _receive = receive;
        _autoRespond = autoRespond;
    }

    public int PendingRequestCount => _pending.Count;

    public void Start()
    {
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    // ── sending ────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a request and await the full JSON-RPC response object (result OR error).
    /// Throws on timeout or disconnect.
    /// </summary>
    public Task<JsonElement> SendRequestAsync(string method, JsonNode? @params, int timeoutMs = 30000)
    {
        var id = $"rqc-{Interlocked.Increment(ref _nextId)}";
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        // JSON-RPC 2.0 forbids an explicit `params: null`; omit when absent.
        if (@params is not null)
            message["params"] = @params.DeepClone();

        // Register under the same canonical key the read loop derives from the
        // response's id node (IdKey == JsonNode.ToJsonString()); registering under the
        // bare string would never match and every request would time out.
        var pending = RegisterPending(IdKey(message["id"]!), method, timeoutMs);
        Write(message, "send", "request", method, id);
        return pending.Task;
    }

    /// <summary>Send a notification (no response expected).</summary>
    public void SendNotification(string method, JsonNode? @params)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null)
            message["params"] = @params.DeepClone();

        Write(message, "send", "notification", method, idNode: null);
    }

    /// <summary>
    /// Send an arbitrary JSON-RPC message verbatim. If it is a request and
    /// <paramref name="waitForResponse"/> is set, awaits the matching response; otherwise
    /// completes with <c>null</c>.
    /// </summary>
    public Task<JsonElement?> SendRawAsync(JsonNode message, bool waitForResponse = false, int timeoutMs = 30000)
    {
        if (message is JsonObject obj && obj["jsonrpc"] is null)
            obj["jsonrpc"] = "2.0";

        var isArray = message is JsonArray;
        var idNode = isArray ? null : message["id"];
        var hasId = idNode is not null;
        var method = isArray ? null : message["method"]?.GetValue<string>();
        var kind = isArray
            ? "batch"
            : method is not null
                ? (hasId ? "request" : "notification")
                : "response";

        Task<JsonElement?> resultTask = Task.FromResult<JsonElement?>(null);
        if (waitForResponse && kind == "request" && idNode is not null)
        {
            var pending = RegisterPending(IdKey(idNode), method ?? "", timeoutMs);
            resultTask = WrapNullable(pending.Task);
        }

        Write(message, "send", kind, method, idNode);
        return resultTask;
    }

    /// <summary>Manually respond to a server-&gt;client request (used when auto-respond is off).</summary>
    public void Respond(JsonNode id, JsonNode? result, JsonNode? error)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
        };
        if (error is not null)
            message["error"] = error.DeepClone();
        else
            message["result"] = result?.DeepClone();

        Write(message, "send", "response", method: null, idNode: id);
    }

    // ── receiving ──────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var chunk = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _receive.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (n <= 0)
                    break; // EOF

                EnsureCapacity(_accLen + n);
                Buffer.BlockCopy(chunk, 0, _acc, _accLen, n);
                _accLen += n;
                DrainFrames();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            FailAllPending(new IOException($"LSP read loop failed: {ex.Message}", ex));
            Disconnected?.Invoke(ex.Message);
            return;
        }

        FailAllPending(new IOException("Server closed its output stream."));
        Disconnected?.Invoke("eof");
    }

    private void DrainFrames()
    {
        while (true)
        {
            int headerEnd = IndexOf(_acc, _accLen, HeaderSeparator);
            if (headerEnd < 0)
                return;

            string header = Encoding.ASCII.GetString(_acc, 0, headerEnd);
            int bodyStart = headerEnd + HeaderSeparator.Length;
            int contentLength = ParseContentLength(header);

            if (contentLength < 0)
            {
                // Unexpected header; skip past it to resync.
                ShiftLeft(bodyStart);
                continue;
            }

            if (_accLen - bodyStart < contentLength)
                return; // wait for more bytes

            string body = Encoding.UTF8.GetString(_acc, bodyStart, contentLength);
            ShiftLeft(bodyStart + contentLength);
            HandleIncoming(body);
        }
    }

    private void HandleIncoming(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            Emit("recv", "error", null, null, "Failed to parse server frame", ToElement(new JsonObject
            {
                ["error"] = ex.Message,
                ["body"] = body,
            }));
            return;
        }

        if (node is not JsonObject msg)
        {
            // Batch or non-object frame; surface it generically.
            Emit("recv", node is JsonArray ? "batch" : "info", null, null, "non-object frame", ToElement(node));
            return;
        }

        var idNode = msg["id"];
        var hasResultOrError = msg.ContainsKey("result") || msg.ContainsKey("error");
        var methodNode = msg["method"];

        // Response: has id, has result/error, no method.
        if (idNode is not null && hasResultOrError && methodNode is null)
        {
            var idElement = ToElement(idNode);
            // A JSON-RPC response carries no method of its own, so recover the originating
            // request's method from the pending entry to tie the response back to its request.
            string? requestMethod = null;
            if (_pending.TryRemove(IdKey(idNode), out var pending))
            {
                requestMethod = string.IsNullOrEmpty(pending.Method) ? null : pending.Method;
                pending.Complete(ToElement(msg)!.Value);
            }
            var isError = msg.ContainsKey("error");
            var label = isError ? "error response" : "response";
            var summary = requestMethod is null
                ? $"{label} (id {idNode})"
                : $"{label}: {requestMethod} (id {idNode})";
            Emit("recv", "response", requestMethod, idElement, summary, ToElement(msg));
            return;
        }

        // Request: has method and id (server expects a response).
        if (methodNode is not null && idNode is not null)
        {
            var method = methodNode.GetValue<string>();
            Emit("recv", "request", method, ToElement(idNode), $"server→client request: {method} (id {idNode})", ToElement(msg));
            if (_autoRespond)
                AutoRespond(msg, method, idNode);
            return;
        }

        // Notification: method, no id.
        if (methodNode is not null)
        {
            var method = methodNode.GetValue<string>();
            Emit("recv", "notification", method, null, $"server→client notification: {method}", ToElement(msg));
            return;
        }

        Emit("recv", "info", null, null, "unrecognized frame", ToElement(msg));
    }

    private void AutoRespond(JsonObject msg, string method, JsonNode idNode)
    {
        JsonNode? result = null;
        if (method == "workspace/configuration")
        {
            // Reply with one null per requested item ("use defaults").
            var items = msg["params"]?["items"] as JsonArray;
            var array = new JsonArray();
            for (int i = 0; i < (items?.Count ?? 0); i++)
                array.Add((JsonNode?)null);
            result = array;
        }
        // Other infra requests (workDoneProgress/create, registerCapability, showMessageRequest, ...)
        // accept a null result.
        Respond(idNode, result, error: null);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private PendingRequest RegisterPending(string idKey, string method, int timeoutMs)
    {
        var pending = new PendingRequest { Method = method };
        _pending[idKey] = pending;
        if (timeoutMs > 0)
        {
            var timer = new CancellationTokenSource(timeoutMs);
            timer.Token.Register(() =>
            {
                if (_pending.TryRemove(idKey, out var p))
                    p.Fail(new TimeoutException($"Timed out after {timeoutMs}ms waiting for response to '{method}' (id {idKey})."));
                timer.Dispose();
            });
            pending.OnSettled(timer.Dispose);
        }
        return pending;
    }

    private void Write(JsonNode message, string direction, string kind, string? method, JsonNode? idNode)
    {
        if (_disposed)
            throw new InvalidOperationException("Connection disposed; cannot send.");

        var json = message.ToJsonString();
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        lock (_writeLock)
        {
            _send.Write(head, 0, head.Length);
            _send.Write(body, 0, body.Length);
            _send.Flush();
        }

        var summary = kind switch
        {
            "request" => $"client→server request: {method} (id {idNode})",
            "notification" => $"client→server notification: {method}",
            "response" => $"client→server response (id {idNode})",
            "batch" => "client→server batch",
            _ => kind,
        };
        Emit(direction, kind, method, idNode is null ? null : ToElement(idNode), summary, ToElement(message));
    }

    private void Emit(string direction, string kind, string? method, JsonElement? id, string summary, JsonElement? payload)
        => MessageObserved?.Invoke(new ObservedMessage(
            direction, kind, method, id, summary, payload,
            Lspeek.Core.Session.LspDisplayDetail.Describe(kind, method, payload)));

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var p))
                p.Fail(ex);
        }
    }

    private static async Task<JsonElement?> WrapNullable(Task<JsonElement> task)
        => await task.ConfigureAwait(false);

    private static string IdKey(JsonNode id) => id.ToJsonString();

    private static JsonElement? ToElement(JsonNode? node)
        => node is null ? null : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private void EnsureCapacity(int needed)
    {
        if (needed <= _acc.Length)
            return;
        int newSize = _acc.Length;
        while (newSize < needed)
            newSize *= 2;
        Array.Resize(ref _acc, newSize);
    }

    private void ShiftLeft(int count)
    {
        int remaining = _accLen - count;
        if (remaining > 0)
            Buffer.BlockCopy(_acc, count, _acc, 0, remaining);
        _accLen = remaining;
    }

    private static int IndexOf(byte[] haystack, int length, byte[] needle)
    {
        int limit = length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var name = line[..colon].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(line[(colon + 1)..].Trim(), out var length))
                return length;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        FailAllPending(new ObjectDisposedException(nameof(RawLspConnection)));
        try
        {
            if (_readLoop is not null)
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
        _cts.Dispose();
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonElement> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _onSettled;

        /// <summary>Method of the request this entry awaits a response for; used to label the response.</summary>
        public string? Method { get; init; }

        public Task<JsonElement> Task => _tcs.Task;

        public void OnSettled(Action action) => _onSettled = action;

        public void Complete(JsonElement response)
        {
            _onSettled?.Invoke();
            _tcs.TrySetResult(response);
        }

        public void Fail(Exception ex)
        {
            _onSettled?.Invoke();
            _tcs.TrySetException(ex);
        }
    }
}
