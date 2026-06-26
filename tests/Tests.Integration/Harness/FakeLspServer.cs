using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using StreamJsonRpc;

namespace ManualLspClient.Tests.Integration.Harness;

/// <summary>
/// A fake LSP server that communicates over StreamJsonRpc for integration testing.
/// Allows tests to configure request handlers and send server→client notifications/requests.
/// </summary>
public sealed class FakeLspServer : IAsyncDisposable
{
    private static readonly MethodInfo s_requestHandlerMethod =
        typeof(DynamicRequestHandler).GetMethod(nameof(DynamicRequestHandler.Handle))!;
    private static readonly MethodInfo s_requestHandlerNoParamsMethod =
        typeof(DynamicRequestHandlerNoParams).GetMethod(nameof(DynamicRequestHandlerNoParams.Handle))!;
    private static readonly MethodInfo s_notificationHandlerMethod =
        typeof(DynamicNotificationHandler).GetMethod(nameof(DynamicNotificationHandler.Handle))!;
    private static readonly MethodInfo s_notificationHandlerNoParamsMethod =
        typeof(DynamicNotificationHandlerNoParams).GetMethod(nameof(DynamicNotificationHandlerNoParams.Handle))!;

    private readonly JsonRpc _rpc;
    private readonly ConcurrentDictionary<string, Func<JsonElement?, object?>> _requestHandlers = new();
    private readonly ConcurrentQueue<(string Method, JsonElement? Params)> _receivedMessages = new();
    private readonly HashSet<string> _registeredMethods = [];
    private bool _disposed;

    public FakeLspServer(Stream stream, Action<FakeLspServer>? configure = null)
    {
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AllowModificationWhileListening = true;

        // Default handlers for the LSP lifecycle
        RegisterRequestHandler("initialize", _ => new { capabilities = new { } });
        RegisterRequestHandler("shutdown", _ => null);

        // Notification sinks for common client→server notifications
        foreach (var method in CommonClientNotifications)
        {
            RegisterNotificationSink(method);
        }

        configure?.Invoke(this);

        _rpc.StartListening();
    }

    /// <summary>
    /// Registers (or replaces) a handler for a client→server request method.
    /// Registers both 0-param and 1-param overloads so the handler works
    /// whether or not the client sends params.
    /// </summary>
    public void RegisterRequestHandler(string method, Func<JsonElement?, object?> handler)
    {
        _requestHandlers[method] = handler;

        if (_registeredMethods.Add(method))
        {
            // Handler with params (for requests that include a params object)
            var target = new DynamicRequestHandler(this, method);
            _rpc.AddLocalRpcMethod(
                s_requestHandlerMethod,
                target,
                new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });

            // Handler without params (for requests that omit the params field)
            var targetNoParams = new DynamicRequestHandlerNoParams(this, method);
            _rpc.AddLocalRpcMethod(
                s_requestHandlerNoParamsMethod,
                targetNoParams,
                new JsonRpcMethodAttribute(method));
        }
    }

    /// <summary>
    /// Registers a sink that records received notifications from the client.
    /// </summary>
    public void RegisterNotificationSink(string method)
    {
        if (_registeredMethods.Add(method))
        {
            var target = new DynamicNotificationHandler(this, method);
            _rpc.AddLocalRpcMethod(
                s_notificationHandlerMethod,
                target,
                new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });

            var targetNoParams = new DynamicNotificationHandlerNoParams(this, method);
            _rpc.AddLocalRpcMethod(
                s_notificationHandlerNoParamsMethod,
                targetNoParams,
                new JsonRpcMethodAttribute(method));
        }
    }

    /// <summary>
    /// Raised whenever a client→server notification is received, after it has been recorded.
    /// Lets a host (e.g. the out-of-process fake server) react — for example, emit a
    /// <c>window/logMessage</c> once the client sends <c>initialized</c>.
    /// </summary>
    public event Action<string, JsonElement?>? NotificationReceived;

    /// <summary>Completes when the underlying JSON-RPC connection ends (stream closed / disposed).</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>
    /// Sends a notification from the server to the client.
    /// </summary>
    public Task SendNotificationAsync(string method, object? @params = null)
    {
        return _rpc.NotifyWithParameterObjectAsync(method, @params);
    }

    /// <summary>
    /// Sends a request from the server to the client and returns the response.
    /// </summary>
    public Task<JsonElement?> SendRequestAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        return _rpc.InvokeWithParameterObjectAsync<JsonElement?>(method, @params, ct);
    }

    /// <summary>
    /// Gets all messages received by the server (requests and notifications from the client).
    /// </summary>
    public IReadOnlyList<(string Method, JsonElement? Params)> GetReceivedMessages()
    {
        return [.. _receivedMessages];
    }

    private void RecordMessage(string method, JsonElement? @params)
    {
        _receivedMessages.Enqueue((method, @params?.Clone()));
    }

    private void RaiseNotification(string method, JsonElement? @params)
        => NotificationReceived?.Invoke(method, @params?.Clone());

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _rpc.Dispose();
        await Task.CompletedTask;
    }

    private sealed class DynamicRequestHandler(FakeLspServer server, string method)
    {
        public object? Handle(JsonElement? @params)
        {
            server.RecordMessage(method, @params);
            return server._requestHandlers.TryGetValue(method, out var handler)
                ? handler(@params)
                : null;
        }
    }

    private sealed class DynamicRequestHandlerNoParams(FakeLspServer server, string method)
    {
        public object? Handle()
        {
            server.RecordMessage(method, null);
            return server._requestHandlers.TryGetValue(method, out var handler)
                ? handler(null)
                : null;
        }
    }

    private sealed class DynamicNotificationHandler(FakeLspServer server, string method)
    {
        public void Handle(JsonElement? @params)
        {
            server.RecordMessage(method, @params);
            server.RaiseNotification(method, @params);
        }
    }

    private sealed class DynamicNotificationHandlerNoParams(FakeLspServer server, string method)
    {
        public void Handle()
        {
            server.RecordMessage(method, null);
            server.RaiseNotification(method, null);
        }
    }

    private static readonly string[] CommonClientNotifications =
    [
        "initialized",
        "exit",
        "textDocument/didOpen",
        "textDocument/didClose",
        "textDocument/didChange",
        "workspace/didChangeConfiguration",
    ];
}
