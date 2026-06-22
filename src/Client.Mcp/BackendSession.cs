using ManualLspClient.Protocol;

namespace ManualLspClient.Mcp;

/// <summary>
/// Owns this MCP process's single private backend instance. The backend (and thus the LSP
/// server) is spawned lazily on first use and reused across tool calls, then stopped when the
/// host shuts down (DI disposes this singleton, which closes the backend's stdin).
/// </summary>
public sealed class BackendSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;
    private bool _disposed;

    /// <summary>Returns the started backend client, spawning the backend on first call.</summary>
    public async Task<BackendClient> GetAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return _client;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is null)
            {
                var client = new BackendClient(new BackendClientOptions
                {
                    OnStderr = line => Console.Error.WriteLine($"[backend] {line}"),
                });
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                _client = client;
            }
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
    }
}
