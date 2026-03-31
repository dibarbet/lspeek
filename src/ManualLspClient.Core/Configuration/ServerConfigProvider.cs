using System.Reflection;
using System.Text.Json;

namespace ManualLspClient.Core.Configuration;

/// <summary>
/// Loads server configurations from embedded defaults and optional user config file.
/// </summary>
public class ServerConfigProvider
{
    private static readonly string UserConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lsp-client");

    private static readonly string UserConfigPath = Path.Combine(UserConfigDirectory, "servers.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, ServerConfig> _servers = new(StringComparer.OrdinalIgnoreCase);

    private ServerConfigProvider() { }

    public static ServerConfigProvider Load()
    {
        var provider = new ServerConfigProvider();
        provider.LoadEmbeddedServers();
        provider.LoadUserServers();
        return provider;
    }

    /// <summary>
    /// Resolves a server by built-in name or by file path.
    /// </summary>
    public ServerConfig Resolve(string serverNameOrPath)
    {
        // If it's a path to an existing file, load it directly
        if (File.Exists(serverNameOrPath))
        {
            return LoadFromFile(serverNameOrPath);
        }

        // Otherwise, look up by name
        if (_servers.TryGetValue(serverNameOrPath, out var config))
        {
            return config;
        }

        var available = string.Join(", ", _servers.Keys.OrderBy(k => k));
        throw new ArgumentException(
            $"Unknown server '{serverNameOrPath}'. Available built-in servers: {available}. " +
            $"Or provide a path to a server config JSON file.");
    }

    public IReadOnlyDictionary<string, ServerConfig> GetAllServers() => _servers;

    private static ServerConfig LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize server config from '{filePath}'.");
        if (string.IsNullOrEmpty(config.Name))
            config.Name = Path.GetFileNameWithoutExtension(filePath);
        return config;
    }

    private void LoadEmbeddedServers()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("EmbeddedServers.json"))
            ?? throw new InvalidOperationException("Embedded server configuration resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var doc = JsonDocument.Parse(stream);
        ParseServersDocument(doc);
    }

    private void LoadUserServers()
    {
        if (!File.Exists(UserConfigPath))
            return;

        try
        {
            var json = File.ReadAllText(UserConfigPath);
            var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            ParseServersDocument(doc);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load user config from {UserConfigPath}: {ex.Message}");
        }
    }

    private void ParseServersDocument(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("servers", out var serversElement))
            return;

        foreach (var serverProperty in serversElement.EnumerateObject())
        {
            var config = JsonSerializer.Deserialize<ServerConfig>(serverProperty.Value.GetRawText(), JsonOptions);
            if (config is not null)
            {
                config.Name = serverProperty.Name;
                _servers[serverProperty.Name] = config;
            }
        }
    }
}
