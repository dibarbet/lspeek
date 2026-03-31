using System.Text.Json;

namespace ManualLspClient.Core.Session;

/// <summary>
/// A single message in the LSP session log.
/// </summary>
public record SessionMessage
{
    public required Transport.MessageDirection Direction { get; init; }
    public required Transport.MessageType MessageType { get; init; }
    public required string Method { get; init; }
    public int? Id { get; init; }
    public JsonElement? Json { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns the JSON body as a pretty-printed string.
    /// </summary>
    public string GetFormattedJson()
    {
        if (Json is null) return "null";
        return JsonSerializer.Serialize(Json.Value, new JsonSerializerOptions { WriteIndented = true });
    }
}
