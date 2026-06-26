using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lspeek.Core.Configuration;

/// <summary>
/// Source-generated JSON metadata for <see cref="ServerConfig"/> so the backend can
/// load named/file server configs under Native AOT without reflection-based serialization.
/// Mirrors the reader behavior the provider previously configured (camelCase, comment-
/// and trailing-comma tolerant) for hand-authored config files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ServerConfig))]
internal partial class ServerConfigJsonContext : JsonSerializerContext;
