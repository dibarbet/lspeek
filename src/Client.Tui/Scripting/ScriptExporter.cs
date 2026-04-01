using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            Params = NormalizeParams(msg.Method, msg.Json)
        }).ToList();

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        File.WriteAllText(outputPath, json);
    }

    private static JsonElement? NormalizeParams(string method, JsonElement? @params)
    {
        if (!method.Equals("initialize", StringComparison.OrdinalIgnoreCase) || @params is null)
            return @params;

        if (@params.Value.ValueKind != JsonValueKind.Object)
            return @params;

        var json = JsonNode.Parse(@params.Value.GetRawText())?.AsObject();
        if (json is null)
            return @params;

        json["processId"] = null;
        return JsonSerializer.SerializeToElement(json);
    }
}
