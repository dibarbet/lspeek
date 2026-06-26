using System.Text.Json;
using Lspeek.Protocol;

namespace Lspeek.Tui.Session;

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

    /// <summary>
    /// Single ingestion entry point for backend wire records. Stderr records are grouped into a
    /// contiguous stderr entry; all other records are projected to a <see cref="SessionMessage"/>.
    /// </summary>
    public void AddRecord(LspMessageRecord record)
    {
        if (SessionMessage.IsStderrRecord(record))
            AddStderrLine(SessionMessage.GetStderrText(record));
        else
            Add(SessionMessage.FromRecord(record));
    }

    public void Add(SessionMessage message)
    {
        lock (_lock)
        {
            message.Status = ComputeStatus(message);

            // If this is a response, mark the corresponding request as OK/Error
            if (message.IsResponse && message.Id.HasValue)
            {
                var request = _messages.LastOrDefault(m =>
                    m.IsRequest &&
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
                IsSent = false,
                Kind = "stderr",
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
        bool? isSent = null,
        string? kind = null,
        string? method = null)
    {
        lock (_lock)
        {
            IEnumerable<SessionMessage> query = _messages;

            if (isSent.HasValue)
                query = query.Where(m => m.IsSent == isSent.Value);

            if (kind is not null)
                query = query.Where(m => m.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

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
        return GetFiltered(isSent: true);
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
        if (message.IsRequest)
        {
            if (message.IsSent)
                return MessageStatus.Pending;

            // Server→client request
            return MessageStatus.Info;
        }

        if (message.IsResponse)
        {
            // Check if the response contains an error
            if (message.Body is { ValueKind: JsonValueKind.Object } body &&
                body.TryGetProperty("code", out _))
            {
                return MessageStatus.Error;
            }
            return MessageStatus.Ok;
        }

        if (message.IsNotification)
        {
            if (message.IsSent)
                return MessageStatus.Sent;

            // Server notifications — classify by method and severity
            if (message.Method == "textDocument/publishDiagnostics")
                return MessageStatus.Warn;

            // Map window/logMessage and window/showMessage by LSP MessageType severity
            if (message.Method is "window/logMessage" or "window/showMessage")
            {
                if (message.Body is { ValueKind: JsonValueKind.Object } body
                    && body.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.Number)
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
