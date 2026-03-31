using ManualLspClient.Core.Transport;

namespace ManualLspClient.Core.Session;

/// <summary>
/// Thread-safe, append-only log of all messages in an LSP session.
/// Automatically computes message statuses.
/// Stderr output is recorded as its own log entries, grouped by contiguous output.
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
            // Compute status
            message.Status = ComputeStatus(message);

            // If this is a response, mark the corresponding request as OK/Error
            if (message.MessageType == MessageType.Response && message.Id.HasValue)
            {
                var request = _messages.LastOrDefault(m =>
                    m.MessageType == MessageType.Request &&
                    m.Id == message.Id &&
                    m.Status == MessageStatus.Pending);
                if (request is not null)
                {
                    request.Status = message.Status;
                }
            }

            _messages.Add(message);
        }
        MessageAdded?.Invoke(message);
    }

    /// <summary>
    /// Records a stderr line from the server process.
    /// If the most recent log entry is a stderr block, the line is appended to it.
    /// Otherwise a new stderr entry is created, so LSP messages break up contiguous stderr.
    /// </summary>
    public void AddStderrLine(string line)
    {
        lock (_lock)
        {
            if (_messages.Count > 0 && _messages[^1].IsStderr)
            {
                _messages[^1].StderrLines.Add(line);
                return;
            }

            var stderrMessage = new SessionMessage
            {
                Direction = MessageDirection.Received,
                MessageType = MessageType.Stderr,
                Method = "stderr",
                Status = MessageStatus.Stderr,
            };
            stderrMessage.StderrLines.Add(line);
            _messages.Add(stderrMessage);
        }
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

    private static MessageStatus ComputeStatus(SessionMessage message)
    {
        if (message.MessageType == MessageType.Request)
        {
            if (message.Direction == MessageDirection.Sent)
                return MessageStatus.Pending;

            // Server→client request
            return MessageStatus.Info;
        }

        if (message.MessageType == MessageType.Response)
        {
            // Check if the response contains an error
            if (message.Json.HasValue)
            {
                var json = message.Json.Value;
                if (json.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    json.TryGetProperty("code", out _))
                {
                    return MessageStatus.Error;
                }
            }
            return MessageStatus.Ok;
        }

        if (message.MessageType == MessageType.Notification)
        {
            if (message.Direction == MessageDirection.Sent)
                return MessageStatus.Sent;

            // Server notifications — classify by method and severity
            if (message.Method == "textDocument/publishDiagnostics")
                return MessageStatus.Warn;

            // Map window/logMessage and window/showMessage by MessageType severity
            if (message.Method is "window/logMessage" or "window/showMessage")
            {
                if (message.Json.HasValue && message.Json.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                    && message.Json.Value.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    // LSP MessageType: 1=Error, 2=Warning, 3=Info, 4=Log
                    return typeProp.GetInt32() switch
                    {
                        1 => MessageStatus.Error,
                        2 => MessageStatus.Warn,
                        _ => MessageStatus.Info
                    };
                }
            }

            return MessageStatus.Info;
        }

        return MessageStatus.Sent;
    }
}
