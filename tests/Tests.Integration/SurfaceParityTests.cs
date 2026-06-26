using System.Text.RegularExpressions;
using Xunit;

namespace ManualLspClient.Tests.Integration;

/// <summary>
/// Guards the unified action surface: the canvas extension (JS) and the MCP server (C#) must
/// expose the exact same set of action/tool names as the backend API. These names are the
/// contract an agent drives the LSP server with, so drift between frontends should fail the build.
/// </summary>
public class SurfaceParityTests
{
    private static readonly HashSet<string> Canonical = new(StringComparer.Ordinal)
    {
        "start_server",
        "stop_server",
        "server_status",
        "lsp_request",
        "lsp_notify",
        "send_raw",
        "respond_to_request",
        "get_messages",
        "wait_for_message",
        "clear_messages",
        "help",
    };

    [Fact]
    public void McpTools_ExposeTheCanonicalSurface()
    {
        var source = ReadRepoFile(Path.Combine("src", "Client.Mcp", "LspTools.cs"));
        var names = Matches(source, """McpServerTool\(Name\s*=\s*"([a-z_]+)"\)""");

        Assert.Equal(Canonical, names);
    }

    [Fact]
    public void CanvasActions_ExposeTheCanonicalSurface()
    {
        var source = ReadRepoFile(Path.Combine(".github", "extensions", "lspeek-canvas", "extension.mjs"));
        // Action declarations appear as a `name: "..."` property on its own line within each action object.
        var names = Matches(source, """^\s*name:\s*"([a-z_]+)",""", RegexOptions.Multiline);

        Assert.Equal(Canonical, names);
    }

    [Fact]
    public void CanvasAndMcp_HaveIdenticalSurfaces()
    {
        var mcp = Matches(ReadRepoFile(Path.Combine("src", "Client.Mcp", "LspTools.cs")),
            """McpServerTool\(Name\s*=\s*"([a-z_]+)"\)""");
        var canvas = Matches(ReadRepoFile(Path.Combine(".github", "extensions", "lspeek-canvas", "extension.mjs")),
            """^\s*name:\s*"([a-z_]+)",""", RegexOptions.Multiline);

        Assert.Equal(mcp, canvas);
    }

    private static HashSet<string> Matches(string source, string pattern, RegexOptions options = RegexOptions.None)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(source, pattern, options))
            set.Add(m.Groups[1].Value);
        return set;
    }

    private static string ReadRepoFile(string relativePath)
    {
        var full = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(full), $"Expected repo file not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "lspeek.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (lspeek.slnx) from " + AppContext.BaseDirectory);
    }
}
