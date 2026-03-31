using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using System.Text.Json;

namespace ManualLspClient.Tui.Scripting;

/// <summary>
/// Exports sent messages from a session log as a replayable JSON script file.
/// </summary>
public static class ScriptExporter
{
    public static void Export(SessionLog log, string outputPath)
    {
        var sentMessages = log.GetSentMessages();

        var entries = sentMessages
            .Where(msg => !msg.IsStderr)
            .Select(msg => new ScriptEntry
        {
            Type = msg.MessageType == MessageType.Notification ? "notification" : "request",
            Method = msg.Method,
            Params = msg.Json
        }).ToList();

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        File.WriteAllText(outputPath, json);
    }
}
