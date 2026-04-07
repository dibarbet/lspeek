using System.Text.Json;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using Nerdbank.Streams;

namespace ManualLspClient.Tests.Integration.Harness;

/// <summary>
/// Integration test harness that wires a real LspConnection + SessionLog to a FakeLspServer
/// over in-memory streams. Replicates the same wiring that LspSession performs.
/// </summary>
public sealed class LspTestHarness : IAsyncDisposable
{
    public LspConnection Connection { get; }
    public SessionLog Log { get; } = new();
    public WorkDoneProgressTracker ProgressTracker { get; } = new();
    public FakeLspServer Server { get; }

    private readonly Stream _clientStream;
    private readonly Stream _serverStream;

    private LspTestHarness(
        LspConnection connection,
        FakeLspServer server,
        Stream clientStream,
        Stream serverStream)
    {
        Connection = connection;
        Server = server;
        _clientStream = clientStream;
        _serverStream = serverStream;

        // Wire connection events → session log (same as LspSession constructor)
        Connection.MessageTraced += (direction, msgType, method, id, json) =>
        {
            Log.Add(new SessionMessage
            {
                Direction = direction,
                MessageType = msgType,
                Method = method,
                Id = id,
                Json = json
            });
        };

        Log.MessageAdded += ProgressTracker.OnMessageAdded;

        Connection.Disconnected += reason =>
        {
            Log.Add(new SessionMessage
            {
                Direction = MessageDirection.Received,
                MessageType = MessageType.Notification,
                Method = "$/connection.disconnected",
                Json = JsonSerializer.SerializeToElement(new { reason }),
                Status = MessageStatus.Error
            });
        };
    }

    /// <summary>
    /// Creates a test harness with a FakeLspServer connected over in-memory streams.
    /// </summary>
    public static LspTestHarness Create(Action<FakeLspServer>? configureServer = null)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new FakeLspServer(serverStream, configureServer);
        var connection = new LspConnection(clientStream, clientStream);
        return new LspTestHarness(connection, server, clientStream, serverStream);
    }

    /// <summary>
    /// Sends a request through the real LspConnection and returns the response.
    /// </summary>
    public Task<JsonElement> SendRequestAsync(string method, JsonElement? @params = null, CancellationToken ct = default)
        => Connection.SendRequestAsync(method, @params, ct);

    /// <summary>
    /// Sends a notification through the real LspConnection.
    /// </summary>
    public Task SendNotificationAsync(string method, JsonElement? @params = null, CancellationToken ct = default)
        => Connection.SendNotificationAsync(method, @params, ct);

    /// <summary>
    /// Waits for the session log to contain a message matching the predicate.
    /// Useful for server→client messages that arrive asynchronously.
    /// </summary>
    public async Task WaitForLogEntryAsync(
        Func<SessionMessage, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Check existing messages first
        if (Log.GetAll().Any(predicate))
            return;

        void OnMessage(SessionMessage msg)
        {
            if (predicate(msg))
                tcs.TrySetResult();
        }

        Log.MessageAdded += OnMessage;
        try
        {
            // Re-check after subscribing to avoid race
            if (Log.GetAll().Any(predicate))
                return;

            await using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            await tcs.Task;
        }
        finally
        {
            Log.MessageAdded -= OnMessage;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Server.DisposeAsync();
        await _clientStream.DisposeAsync();
        await _serverStream.DisposeAsync();
    }
}
