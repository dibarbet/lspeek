using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lspeek.Tui.Scripting;

/// <summary>
/// Represents a single entry in a JSON script file.
/// </summary>
public class ScriptEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "request"; // "request" or "notification"

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// Represents a JSON script file containing an array of LSP messages to send.
/// </summary>
public class ScriptFile
{
    public List<ScriptEntry> Entries { get; set; } = [];

    public bool EndsWithShutdownAndExit()
    {
        if (Entries.Count < 2)
            return false;

        return IsRequest(Entries[^2], "shutdown")
            && IsNotification(Entries[^1], "exit");
    }

    public bool EndsWithShutdownRequest()
    {
        return Entries.Count > 0 && IsRequest(Entries[^1], "shutdown");
    }

    public bool EndsWithExitNotification()
    {
        return Entries.Count > 0 && IsNotification(Entries[^1], "exit");
    }

    public static ScriptFile Load(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<ScriptEntry>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? [];

        return new ScriptFile { Entries = entries };
    }

    private static bool IsRequest(ScriptEntry entry, string method)
    {
        return entry.Type.Equals("request", StringComparison.OrdinalIgnoreCase)
            && entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNotification(ScriptEntry entry, string method)
    {
        return entry.Type.Equals("notification", StringComparison.OrdinalIgnoreCase)
            && entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase);
    }
}
