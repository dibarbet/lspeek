using System.Runtime.InteropServices;
using System.Text.Json;
using ManualLspClient.Protocol;
using Xunit;

namespace ManualLspClient.Tests.Integration.E2E;

/// <summary>
/// Shared setup for the full end-to-end tests. These drive the <em>real</em> <c>lspeek-http</c>
/// backend (spawned by <see cref="BackendClient"/>) and the MCP tool surface against a real — but
/// fake and deterministic — child LSP server process (<c>lspeek-fake-lsp</c>), so nothing here is
/// mocked: the HTTP+SSE backend, the JSON-RPC framing, and the server child are all genuine.
///
/// The fixture locates the two build outputs the tests need and:
///  • points backend discovery at the freshly-built <c>lspeek-http</c> via the <c>LSPEEK_HTTP</c>
///    environment variable (so <see cref="BackendLauncher"/> finds it without a packaged tool); and
///  • writes a server-config JSON that <c>start_server</c> resolves to spawn the fake server.
/// </summary>
public sealed class BackendE2EFixture : IDisposable
{
    private const string BackendName = "lspeek-http";
    private const string FakeServerName = "lspeek-fake-lsp";

    private readonly string _tempDir;

    /// <summary>Absolute path to a server-config JSON pointing at the fake LSP server.</summary>
    public string FakeServerConfigPath { get; }

    public BackendE2EFixture()
    {
        var repoRoot = FindRepoRoot();

        // Point backend discovery at the in-repo managed build of lspeek-http. Passing the directory
        // lets BackendLauncher pick the apphost or the dll (run via `dotnet exec`) as appropriate.
        var backendDir = LocateOutputDirectory(
            Path.Combine(repoRoot, "src", "Client.Backend", "bin"), BackendName,
            "Build the solution (or Client.Mcp) first so lspeek-http exists.");
        Environment.SetEnvironmentVariable(BackendLauncher.EnvironmentVariable, backendDir);

        // Build a server-config JSON that resolves to the fake LSP server process.
        var (command, arguments) = ResolveLaunch(
            Path.Combine(repoRoot, "tests", "FakeLspServer.Host", "bin"), FakeServerName,
            "Build FakeLspServer.Host first so lspeek-fake-lsp exists.");

        _tempDir = Directory.CreateTempSubdirectory("lspeek-e2e-").FullName;
        FakeServerConfigPath = Path.Combine(_tempDir, "fake-server.json");
        var config = new { name = "fake", command, arguments };
        File.WriteAllText(FakeServerConfigPath, JsonSerializer.Serialize(config, ConfigJson));
    }

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Finds the newest build output dir under <paramref name="binRoot"/> that contains the host.</summary>
    private static string LocateOutputDirectory(string binRoot, string baseName, string hint)
    {
        if (!Directory.Exists(binRoot))
            throw new InvalidOperationException($"'{binRoot}' does not exist. {hint}");

        var dll = Directory
            .EnumerateFiles(binRoot, baseName + ".dll", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not find '{baseName}.dll' under '{binRoot}'. {hint}");

        return dll.DirectoryName!;
    }

    /// <summary>Resolves how to launch a host: prefer the native apphost, else <c>dotnet exec dll</c>.</summary>
    private static (string Command, string[] Arguments) ResolveLaunch(string binRoot, string baseName, string hint)
    {
        var dir = LocateOutputDirectory(binRoot, baseName, hint);

        var apphost = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? baseName + ".exe"
            : baseName);
        if (File.Exists(apphost))
            return (apphost, []);

        return ("dotnet", ["exec", Path.Combine(dir, baseName + ".dll")]);
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

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>
/// Groups the E2E classes into one collection so they share a single <see cref="BackendE2EFixture"/>
/// and run sequentially — each test spawns real backend + server processes, so we avoid stacking
/// dozens of child processes in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class BackendE2ECollection : ICollectionFixture<BackendE2EFixture>
{
    public const string Name = "Backend E2E";
}
