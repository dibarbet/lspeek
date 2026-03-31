using ManualLspClient.Core.Transport;

namespace ManualLspClient.Core.Session;

/// <summary>
/// Thread-safe, append-only log of all messages in an LSP session.
/// </summary>
public class SessionLog
{
    private readonly List<SessionMessage> _messages = [];
    private readonly object _lock = new();

    /// <summary>
    /// Raised when a new message is added to the log.
    /// </summary>
    public event Action<SessionMessage>? MessageAdded;

    public void Add(SessionMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
        }
        MessageAdded?.Invoke(message);
    }

    public IReadOnlyList<SessionMessage> GetAll()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }

    public IReadOnlyList<SessionMessage> GetFiltered(
        MessageDirection? direction = null,
        MessageType? messageType = null,
        string? method = null)
    {
        lock (_lock)
        {
            IEnumerable<SessionMessage> query = _messages;

            if (direction.HasValue)
                query = query.Where(m => m.Direction == direction.Value);

            if (messageType.HasValue)
                query = query.Where(m => m.MessageType == messageType.Value);

            if (method is not null)
                query = query.Where(m => m.Method.Equals(method, StringComparison.OrdinalIgnoreCase));

            return [.. query];
        }
    }

    /// <summary>
    /// Returns only the sent messages (for script export).
    /// </summary>
    public IReadOnlyList<SessionMessage> GetSentMessages()
    {
        return GetFiltered(direction: MessageDirection.Sent);
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _messages.Count;
            }
        }
    }
}
