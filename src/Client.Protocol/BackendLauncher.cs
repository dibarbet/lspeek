using System.Runtime.InteropServices;

namespace ManualLspClient.Protocol;

/// <summary>
/// Locates the bundled <c>lspeek-backend</c> so a frontend can spawn its own private backend
/// instance. Every .NET frontend always ships the backend in a <c>backend/</c> folder beside
/// itself (see <c>src/Bundle.Backend.targets</c>): packaged, self-contained tools bundle the
/// native-AOT backend executable, while the in-repo build output bundles the framework-dependent
/// backend dll (run via <c>dotnet exec</c>). The <c>LSPEEK_BACKEND</c> environment variable
/// overrides this for advanced/local scenarios.
/// </summary>
public static class BackendLauncher
{
    /// <summary>Base name of the backend executable (no extension).</summary>
    public const string ExecutableName = "lspeek-backend";

    /// <summary>Environment variable that overrides backend discovery (file or directory).</summary>
    public const string EnvironmentVariable = "LSPEEK_BACKEND";

    /// <summary>Subdirectory (next to the frontend) where packaged tools bundle the backend.</summary>
    public const string BundledSubdirectory = "backend";

    /// <summary>
    /// Resolves how to launch the backend: a file name plus any prefix arguments. A native
    /// apphost resolves to <c>(path, [])</c>; a framework-dependent dll resolves to
    /// <c>("dotnet", ["exec", dllPath])</c>.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> PrefixArgs) Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured) && TryDirectory(configured) is { } fromDir)
                return fromDir;
            if (File.Exists(configured))
                return AsLaunch(configured);
            throw new BackendException(
                $"Configured backend path '{configured}' (from {EnvironmentVariable}) was not found.");
        }

        var baseDir = AppContext.BaseDirectory;

        // Frontends always bundle the backend in a "backend/" folder beside themselves.
        if (TryDirectory(Path.Combine(baseDir, BundledSubdirectory)) is { } fromBundle)
            return fromBundle;

        throw new BackendException(
            $"Could not locate the bundled lspeek backend ('{ExecutableName}') under " +
            $"'{Path.Combine(baseDir, BundledSubdirectory)}'. Reinstall the tool, or set the " +
            $"{EnvironmentVariable} environment variable to the backend executable or its directory.");
    }

    private static (string, IReadOnlyList<string>)? TryDirectory(string dir)
    {
        var host = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ExecutableName + ".exe"
            : ExecutableName);
        if (File.Exists(host))
            return (host, Array.Empty<string>());

        var dll = Path.Combine(dir, ExecutableName + ".dll");
        if (File.Exists(dll))
            return ("dotnet", new[] { "exec", dll });

        return null;
    }

    private static (string, IReadOnlyList<string>) AsLaunch(string file)
        => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", new[] { "exec", file })
            : (file, Array.Empty<string>());
}
