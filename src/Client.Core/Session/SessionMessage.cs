using System.Text.Json;

namespace ManualLspClient.Core.Session;

/// <summary>
/// Status summary for a session message.
/// </summary>
public enum MessageStatus
{
    Sent,       // Notification sent successfully
    Pending,    // Request sent, awaiting response
    Ok,         // Response received without error
    Error,      // Response received with error
    Info,       // Server notification (informational)
    Warn,       // Server notification (warning/diagnostics)
    Stderr      // Server stderr output
}

/// <summary>
/// A single entry in the LSP session log.
/// Groups related request/response pairs and associated stderr output.
/// </summary>
public class SessionMessage
{
    public required Transport.MessageDirection Direction { get; init; }
    public required Transport.MessageType MessageType { get; init; }
    public required string Method { get; init; }
    public int? Id { get; init; }
    public JsonElement? Json { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// High-level status for display purposes.
    /// </summary>
    public MessageStatus Status { get; set; } = MessageStatus.Sent;

    /// <summary>
    /// Stderr lines. For Stderr-type messages these are the log entry content;
    /// retained on other message types for backward compatibility but no longer populated.
    /// </summary>
    public List<string> StderrLines { get; } = [];

    /// <summary>
    /// Whether this is a stderr log entry.
    /// </summary>
    public bool IsStderr => MessageType == Transport.MessageType.Stderr;

    /// <summary>
    /// Returns the JSON body as a pretty-printed string.
    /// </summary>
    public string GetFormattedJson()
    {
        if (Json is null) return "null";
        return JsonSerializer.Serialize(Json.Value, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Returns a short status label for collapsed log display.
    /// </summary>
    public string GetStatusLabel() => Status switch
    {
        MessageStatus.Sent => "Sent",
        MessageStatus.Pending => "Pending",
        MessageStatus.Ok => "OK",
        MessageStatus.Error => "Error",
        MessageStatus.Info => "INFO",
        MessageStatus.Warn => "WARN",
        MessageStatus.Stderr => "stderr",
        _ => "?"
    };

    /// <summary>
    /// Returns the Spectre.Console color for this status.
    /// </summary>
    public string GetStatusColor() => Status switch
    {
        MessageStatus.Ok => "green",
        MessageStatus.Error => "red",
        MessageStatus.Pending => "yellow",
        MessageStatus.Warn => "yellow",
        MessageStatus.Info => "blue",
        MessageStatus.Stderr => "red",
        _ => "dim"
    };
}
