using System.Collections.Concurrent;

namespace Lspeek.Backend.Hosting;

/// <summary>
/// Tracks <see cref="BackendInstance"/>s keyed by <c>instanceId</c>. A single backend process
/// can host several instances (matching the canvas API); each frontend defaults to one.
/// </summary>
public sealed class InstanceManager : IAsyncDisposable
{
    public const string DefaultInstanceId = "default";

    private readonly ConcurrentDictionary<string, BackendInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>Get (creating if needed) the instance for the given id.</summary>
    public BackendInstance Get(string? instanceId)
    {
        var id = string.IsNullOrWhiteSpace(instanceId) ? DefaultInstanceId : instanceId;
        return _instances.GetOrAdd(id, static key => new BackendInstance(key));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances.Values)
            await instance.DisposeAsync().ConfigureAwait(false);
        _instances.Clear();
    }
}
