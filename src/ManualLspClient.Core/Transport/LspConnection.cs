using System.Text.Json;
using System.Reflection;
using StreamJsonRpc;

namespace ManualLspClient.Core.Transport;

/// <summary>
/// Wraps a StreamJsonRpc connection over LSP-framed stdio streams.
/// Registers catch-all handlers for server-sent notifications and requests.
/// </summary>
public class LspConnection : IAsyncDisposable
{
    private static readonly MethodInfo NotificationHandlerMethod = typeof(NotificationRpcHandler).GetMethod(nameof(NotificationRpcHandler.Handle))!;
    private static readonly MethodInfo RequestHandlerMethod = typeof(RequestRpcHandler).GetMethod(nameof(RequestRpcHandler.Handle))!;

    private readonly JsonRpc _rpc;
    private bool _disposed;
    private int _nextRequestId;

    /// <summary>
    /// Raised when any message is sent or received (for session logging).
    /// </summary>
    public event Action<MessageDirection, MessageType, string, int?, JsonElement?>? MessageTraced;

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
        MessageTraced?.Invoke(MessageDirection.Sent, MessageType.Request, method, id, paramsElement);

        try
        {
            var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(method, paramsElement, cancellationToken);
            var resultElement = CloneJsonElement(result);
            MessageTraced?.Invoke(MessageDirection.Received, MessageType.Response, method, id, resultElement);
            return resultElement ?? default;
        }
        catch (RemoteInvocationException ex)
        {
            var errorJson = JsonSerializer.SerializeToElement(new
            {
                code = ex.ErrorCode,
                message = ex.Message,
                data = ex.ErrorData?.ToString()
            });
            MessageTraced?.Invoke(MessageDirection.Received, MessageType.Response, method, id, errorJson);
        }
        catch (Exception ex)
        {
            var errorJson = JsonSerializer.SerializeToElement(new
            {
                code = -1,
                message = ex.Message
            });
            MessageTraced?.Invoke(MessageDirection.Received, MessageType.Response, method, id, errorJson);
        }
        return default;
    }

    public Task SendNotificationAsync(string method, JsonElement? @params, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paramsElement = CloneJsonElement(@params);
        MessageTraced?.Invoke(MessageDirection.Sent, MessageType.Notification, method, null, paramsElement);

        return _rpc.NotifyWithParameterObjectAsync(method, paramsElement);
    }

    private void HandleInboundMessage(MessageType messageType, string method, JsonElement? payload)
    {
        var element = CloneJsonElement(payload);
        MessageTraced?.Invoke(MessageDirection.Received, messageType, method, null, element);
    }

    private static JsonElement? CloneJsonElement(JsonElement? value)
    {
        return value?.Clone();
    }

    private sealed class NotificationRpcHandler(LspConnection connection, string method)
    {
        public void Handle(JsonElement? @params)
        {
            connection.HandleInboundMessage(MessageType.Notification, method, @params);
        }
    }

    private sealed class RequestRpcHandler(LspConnection connection, string method)
    {
        public object? Handle(JsonElement? @params)
        {
            connection.HandleInboundMessage(MessageType.Request, method, @params);
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

public enum MessageDirection
{
    Sent,
    Received
}

public enum MessageType
{
    Request,
    Response,
    Notification,
    Stderr
}

