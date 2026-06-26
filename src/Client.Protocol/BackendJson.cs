using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lspeek.Protocol;

/// <summary>
/// Shared JSON serialization options for the backend wire protocol.
/// camelCase property names match the canvas renderer and JS frontends.
/// </summary>
public static class BackendJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
