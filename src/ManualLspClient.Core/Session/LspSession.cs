using System.Text.Json;
using ManualLspClient.Core.Configuration;
using ManualLspClient.Core.Transport;

namespace ManualLspClient.Core.Session;

/// <summary>
/// Manages the full lifecycle of an LSP session: server process, connection, and message log.
/// </summary>
public class LspSession : IAsyncDisposable
{
    private readonly LspServerProcess _process;
    private readonly LspConnection _connection;
    private bool _disposed;

    public SessionLog Log { get; } = new();
    public ServerConfig ServerConfig { get; }
    public int ServerProcessId => _process.ProcessId;
    public bool IsServerRunning => !_process.HasExited;
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Raised when a notification is received from the server.
    /// </summary>
    public event Action<string, JsonElement?>? NotificationReceived;

    /// <summary>
    /// Raised when a stderr line is received from the server.
    /// </summary>
    public event Action<string>? ServerStderrReceived;

    private LspSession(ServerConfig config, LspServerProcess process, LspConnection connection)
    {
        ServerConfig = config;
        _process = process;
        _connection = connection;

        // Wire connection events → session log
        _connection.MessageTraced += (direction, msgType, method, id, json) =>
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

        _connection.NotificationReceived += (method, json) =>
        {
            NotificationReceived?.Invoke(method, json);
        };

        _process.StderrLineReceived += line =>
        {
            ServerStderrReceived?.Invoke(line);
        };
    }

    /// <summary>
    /// Creates and starts a new LSP session.
    /// </summary>
    public static LspSession Start(ServerConfig config)
    {
        var process = LspServerProcess.Start(config);
        var connection = new LspConnection(process.InputStream, process.OutputStream);
        return new LspSession(config, process, connection);
    }

    /// <summary>
    /// Sends the initialize request and initialized notification.
    /// </summary>
    public async Task<JsonElement> InitializeAsync(JsonElement? customParams = null, CancellationToken cancellationToken = default)
    {
        object initParams = customParams.HasValue
            ? (object)customParams.Value
            : BuildDefaultInitializeParams();

        var result = await _connection.SendRequestAsync("initialize", initParams, cancellationToken);
        await _connection.SendNotificationAsync("initialized", new { }, cancellationToken);

        IsInitialized = true;
        return result;
    }

    /// <summary>
    /// Sends an arbitrary request and returns the response.
    /// </summary>
    public Task<JsonElement> SendRequestAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        return _connection.SendRequestAsync(method, @params, cancellationToken);
    }

    /// <summary>
    /// Sends an arbitrary notification.
    /// </summary>
    public Task SendNotificationAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        return _connection.SendNotificationAsync(method, @params, cancellationToken);
    }

    /// <summary>
    /// Sends shutdown request and exit notification for graceful termination.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _connection.SendRequestAsync("shutdown", null, cancellationToken);
            await _connection.SendNotificationAsync("exit", null, cancellationToken);
        }
        catch (Exception)
        {
            // Best effort — server may have already exited
        }
    }

    /// <summary>
    /// Gets the server's stderr output.
    /// </summary>
    public IReadOnlyList<string> GetServerStderr() => _process.GetStderrLines();

    private object BuildDefaultInitializeParams()
    {
        var workspaceUri = new Uri(Environment.CurrentDirectory).AbsoluteUri;
        return new
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
                }
            },
            rootUri = workspaceUri,
            workspaceFolders = new[]
            {
                new { uri = workspaceUri, name = Path.GetFileName(Environment.CurrentDirectory) }
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _connection.DisposeAsync();
        await _process.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
