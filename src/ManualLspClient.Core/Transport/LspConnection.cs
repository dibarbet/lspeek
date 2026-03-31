using System.Text.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace ManualLspClient.Core.Transport;

/// <summary>
/// Wraps a StreamJsonRpc connection over LSP-framed stdio streams.
/// Registers catch-all notification handlers for server-sent notifications.
/// </summary>
public class LspConnection : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private bool _disposed;
    private int _nextRequestId;

    /// <summary>
    /// Raised when a notification is received from the server.
    /// </summary>
    public event Action<string, JsonElement?>? NotificationReceived;

    /// <summary>
    /// Raised when any message is sent or received (for session logging).
    /// </summary>
    public event Action<MessageDirection, MessageType, string, int?, JsonElement?>? MessageTraced;

    /// <summary>
    /// Raised when the JSON-RPC connection is lost.
    /// Provides the reason and any associated exception message.
    /// </summary>
    public event Action<string>? Disconnected;

    public LspConnection(Stream sendStream, Stream receiveStream, IReadOnlyList<string>? serverNotificationMethods = null)
    {
        var formatter = new JsonMessageFormatter();
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
        var methods = serverNotificationMethods ?? DefaultServerNotificationMethods;
        foreach (var method in methods)
        {
            var m = method; // capture for closure
            _rpc.AddLocalRpcMethod(m, new Action<JToken?>(@params =>
            {
                var element = JTokenToJsonElement(@params);
                MessageTraced?.Invoke(MessageDirection.Received, MessageType.Notification, m, null, element);
                NotificationReceived?.Invoke(m, element);
            }));
        }

        // Register handlers for common server→client requests (server expects a response)
        foreach (var method in DefaultServerRequestMethods)
        {
            var m = method;
            _rpc.AddLocalRpcMethod(m, new Func<JToken?, JToken?>(@params =>
            {
                var element = JTokenToJsonElement(@params);
                MessageTraced?.Invoke(MessageDirection.Received, MessageType.Request, m, null, element);
                NotificationReceived?.Invoke(m, element);
                // Return empty success response
                return JToken.Parse("null");
            }));
        }

        _rpc.StartListening();
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var paramsElement = ObjectToJsonElement(@params);
        MessageTraced?.Invoke(MessageDirection.Sent, MessageType.Request, method, id, paramsElement);

        // Convert to JToken for StreamJsonRpc (uses Newtonsoft.Json internally)
        var jTokenParams = ObjectToJToken(@params);

        try
        {
            var result = await _rpc.InvokeWithParameterObjectAsync<JToken>(method, jTokenParams, cancellationToken);
            var resultElement = JTokenToJsonElement(result);
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
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorJson = JsonSerializer.SerializeToElement(new
            {
                code = -1,
                message = ex.Message
            });
            MessageTraced?.Invoke(MessageDirection.Received, MessageType.Response, method, id, errorJson);
            throw;
        }
    }

    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var paramsElement = ObjectToJsonElement(@params);
        MessageTraced?.Invoke(MessageDirection.Sent, MessageType.Notification, method, null, paramsElement);

        var jTokenParams = ObjectToJToken(@params);
        return _rpc.NotifyWithParameterObjectAsync(method, jTokenParams);
    }

    /// <summary>
    /// Dynamically registers a handler for a server notification method.
    /// </summary>
    public void AddNotificationHandler(string method)
    {
        _rpc.AddLocalRpcMethod(method, new Action<JToken?>(@params =>
        {
            var element = JTokenToJsonElement(@params);
            MessageTraced?.Invoke(MessageDirection.Received, MessageType.Notification, method, null, element);
            NotificationReceived?.Invoke(method, element);
        }));
    }

    private static JsonElement? ObjectToJsonElement(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement je) return je;
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Converts any object to a JToken suitable for StreamJsonRpc (Newtonsoft.Json).
    /// Handles System.Text.Json types (JsonElement) by round-tripping through JSON string.
    /// </summary>
    private static JToken? ObjectToJToken(object? value)
    {
        if (value is null) return null;
        if (value is JToken jt) return jt;
        if (value is JsonElement je)
        {
            var json = je.GetRawText();
            return JToken.Parse(json);
        }
        // For anonymous objects / POCOs, serialize via System.Text.Json then parse as JToken
        var serialized = JsonSerializer.Serialize(value);
        return JToken.Parse(serialized);
    }

    private static JsonElement? JTokenToJsonElement(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        var json = token.ToString(Newtonsoft.Json.Formatting.None);
        return JsonDocument.Parse(json).RootElement.Clone();
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

