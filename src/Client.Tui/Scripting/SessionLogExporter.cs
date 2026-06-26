using Lspeek.Tui.Presentation;
using Lspeek.Tui.Session;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lspeek.Tui.Scripting;

/// <summary>
/// Exports the full session log (all sent, received, stderr, and diagnostic messages)
/// as a JSON file for offline searching and analysis.
/// </summary>
public static class SessionLogExporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Export(SessionLog log, string outputPath)
    {
        var messages = log.GetAll();

        var entries = messages.Select(ToEntry).ToList();

        var envelope = new SessionLogFile
        {
            ExportedAt = DateTimeOffset.UtcNow,
            MessageCount = entries.Count,
            Messages = entries
        };

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        File.WriteAllText(outputPath, json);
    }

    private static SessionLogEntry ToEntry(SessionMessage msg)
    {
        if (msg.IsStderr)
        {
            return new SessionLogEntry
            {
                Timestamp = msg.Timestamp,
                Direction = "received",
                MessageType = "stderr",
                Method = "stderr",
                Status = "stderr",
                StderrLines = msg.StderrLines.ToList()
            };
        }

        return new SessionLogEntry
        {
            Timestamp = msg.Timestamp,
            Direction = msg.IsSent ? "sent" : "received",
            MessageType = msg.Kind,
            Method = msg.Method,
            Id = msg.Id,
            Status = MessageStatusStyles.Label(msg.Status).ToLowerInvariant(),
            Body = msg.Body
        };
    }
}

public class SessionLogFile
{
    public DateTimeOffset ExportedAt { get; set; }
    public int MessageCount { get; set; }
    public List<SessionLogEntry> Messages { get; set; } = [];
}

public class SessionLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Direction { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string Method { get; set; } = "";
    public int? Id { get; set; }
    public string Status { get; set; } = "";
    public JsonElement? Body { get; set; }
    public List<string>? StderrLines { get; set; }
}
