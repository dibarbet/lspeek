using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManualLspClient.Tui.Scripting;

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
}
