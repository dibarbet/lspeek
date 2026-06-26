using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Lspeek.Protocol;
using StreamJsonRpc;

namespace Lspeek.Tests.Integration.Harness;

/// <summary>
/// Wraps a StreamJsonRpc connection over LSP-framed stdio streams.
/// Registers catch-all handlers for server-sent notifications and requests.
/// </summary>
/// <remarks>
/// This is a test-only harness component. The production backend drives the server
/// through <c>RawLspConnection</c> in Client.Core; this StreamJsonRpc-based wrapper
/// exists solely to exercise the session log / progress wiring in integration tests.
/// It emits the same <see cref="LspMessageRecord"/> wire shape the backend buffers, so the
/// TUI session log is exercised through its real ingestion path.
/// </remarks>
public class LspConnection : IAsyncDisposable
{
    private static readonly MethodInfo NotificationHandlerMethod = typeof(NotificationRpcHandler).GetMethod(nameof(NotificationRpcHandler.Handle))!;
    private static readonly MethodInfo RequestHandlerMethod = typeof(RequestRpcHandler).GetMethod(nameof(RequestRpcHandler.Handle))!;
    private static readonly JsonElement NullJsonElement = JsonSerializer.SerializeToElement((object?)null);

    private readonly JsonRpc _rpc;
    private bool _disposed;
    private int _nextRequestId;
    private long _seq;

    /// <summary>
    /// Raised when any message is sent or received, shaped as the backend's wire record
    /// (for session logging).
    /// </summary>
    public event Action<LspMessageRecord>? MessageTraced;

    /// <summary>
    /// Raised when the JSON-RPC connection is lost.
    /// Provides the reason and any associated exception message.
    /// </summary>
    public event Action<string>? Disconnected;

    public LspConnection(Stream sendStream, Stream receiveStream)
    {
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(sendStream, receiveStream, formatter);

        _rpc = new JsonRpc(handler);
        _rpc.AllowModificationWhileListening = true;

        _rpc.Disconnected += (_, args) =>
        {
            var reason = args.Reason.ToString();
            var detail = args.Exception?.Message;
            var message = detail is not null ? $"{reason}: {detail}" : reason;
            Disconnected?.Invoke(message);
        };

        // Register handlers for known server→client notification methods
        var methods = DefaultServerNotificationMethods;
        foreach (var method in methods)
        {
            var target = new NotificationRpcHandler(this, method);
            _rpc.AddLocalRpcMethod(
                NotificationHandlerMethod,
                target,
                new JsonRpcMethodAttribute(method)
                {
                    UseSingleObjectParameterDeserialization = true
                });
        }

        // Register handlers for common server→client requests (server expects a response)
        foreach (var method in DefaultServerRequestMethods)
        {
            var target = new RequestRpcHandler(this, method);
            _rpc.AddLocalRpcMethod(
                RequestHandlerMethod,
                target,
                new JsonRpcMethodAttribute(method)
                {
                    UseSingleObjectParameterDeserialization = true
                });
        }

        _rpc.StartListening();
    }

    public async Task<JsonElement> SendRequestAsync(string method, JsonElement? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var paramsElement = CloneJsonElement(@params);
        Trace("send", "request", method, id, "params", paramsElement);

        try
        {
            var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(method, paramsElement, cancellationToken);
            var resultElement = CloneJsonElement(result);
            Trace("recv", "response", method, id, "result", resultElement);
            return resultElement ?? NullJsonElement;
        }
        catch (RemoteInvocationException ex)
        {
            var errorJson = JsonSerializer.SerializeToElement(new
            {
                code = ex.ErrorCode,
                message = ex.Message,
                data = ex.ErrorData?.ToString()
            });
            Trace("recv", "response", method, id, "error", errorJson);
            return errorJson;
        }
        catch (Exception ex)
        {
            var errorJson = JsonSerializer.SerializeToElement(new
            {
                code = -1,
                message = ex.Message
            });
            Trace("recv", "response", method, id, "error", errorJson);
            return errorJson;
        }
    }

    public Task SendNotificationAsync(string method, JsonElement? @params, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paramsElement = CloneJsonElement(@params);
        Trace("send", "notification", method, null, "params", paramsElement);

        return _rpc.NotifyWithParameterObjectAsync(method, paramsElement);
    }

    private void HandleInboundMessage(string kind, string method, JsonElement? payload)
    {
        var element = CloneJsonElement(payload);
        Trace("recv", kind, method, null, "params", element);
    }

    /// <summary>
    /// Emits a <see cref="LspMessageRecord"/> matching the backend wire shape. The inner object is
    /// wrapped under <paramref name="bodyProperty"/> (<c>params</c>/<c>result</c>/<c>error</c>) so
    /// the session log projects it exactly as it does for live backend traffic.
    /// </summary>
    private void Trace(string direction, string kind, string method, int? id, string bodyProperty, JsonElement? body)
    {
        MessageTraced?.Invoke(new LspMessageRecord
        {
            Seq = Interlocked.Increment(ref _seq),
            Time = DateTimeOffset.UtcNow.ToString("O"),
            Direction = direction,
            Kind = kind,
            Method = method,
            Id = id is { } value ? JsonSerializer.SerializeToElement(value) : null,
            Summary = method,
            Payload = WrapBody(bodyProperty, body),
        });
    }

    private static JsonElement WrapBody(string property, JsonElement? body)
    {
        var obj = new JsonObject();
        if (body is { ValueKind: not JsonValueKind.Null } value)
            obj[property] = JsonNode.Parse(value.GetRawText());
        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonElement? CloneJsonElement(JsonElement? value)
    {
        return value?.Clone();
    }

    private sealed class NotificationRpcHandler(LspConnection connection, string method)
    {
        public void Handle(JsonElement? @params)
        {
            connection.HandleInboundMessage("notification", method, @params);
        }
    }

    private sealed class RequestRpcHandler(LspConnection connection, string method)
    {
        public object? Handle(JsonElement? @params)
        {
            connection.HandleInboundMessage("request", method, @params);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rpc.Dispose();
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }

    // Well-known LSP server→client notification methods
    private static readonly string[] DefaultServerNotificationMethods =
    [
        "window/logMessage",
        "window/showMessage",
        "textDocument/publishDiagnostics",
        "$/progress",
        "telemetry/event",
        "window/workDoneProgress/cancel",
    ];

    // Well-known LSP server→client request methods (server expects a response)
    private static readonly string[] DefaultServerRequestMethods =
    [
        "client/registerCapability",
        "client/unregisterCapability",
        "workspace/configuration",
        "workspace/workspaceFolders",
        "window/workDoneProgress/create",
        "window/showMessageRequest",
        "workspace/applyEdit",
        "workspace/codeLens/refresh",
        "workspace/diagnostic/refresh",
        "workspace/inlayHint/refresh",
        "workspace/semanticTokens/refresh",
    ];
}
