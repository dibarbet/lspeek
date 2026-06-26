using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManualLspClient.Protocol;

/// <summary>
/// System.Text.Json source-generation context for every backend wire DTO.
/// Using generated metadata (instead of reflection) keeps the backend's
/// serialization Native-AOT safe and trim-friendly. The options here mirror
/// <see cref="BackendJson.Options"/> so the wire format (camelCase, null-skipping,
/// comment/trailing-comma tolerant) is identical across all frontends.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LspMessageRecord))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(StartServerRequest))]
[JsonSerializable(typeof(StartServerResponse))]
[JsonSerializable(typeof(ServerCandidate))]
[JsonSerializable(typeof(LspRequestInput))]
[JsonSerializable(typeof(LspRequestResult))]
[JsonSerializable(typeof(LspNotifyInput))]
[JsonSerializable(typeof(SendRawInput))]
[JsonSerializable(typeof(SendRawResult))]
[JsonSerializable(typeof(RespondInput))]
[JsonSerializable(typeof(GetMessagesQuery))]
[JsonSerializable(typeof(GetMessagesResult))]
[JsonSerializable(typeof(WaitForMessageInput))]
[JsonSerializable(typeof(WaitForMessageResult))]
[JsonSerializable(typeof(InstanceState))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(string))]
public partial class BackendJsonContext : JsonSerializerContext;
