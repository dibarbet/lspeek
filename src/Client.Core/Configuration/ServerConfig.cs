namespace Lspeek.Core.Configuration;

/// <summary>
/// Configuration for launching an LSP server.
/// </summary>
public class ServerConfig
{
    public string Name { get; set; } = "";
    public required string Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
}
