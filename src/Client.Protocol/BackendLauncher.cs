using System.Runtime.InteropServices;

namespace ManualLspClient.Protocol;

/// <summary>
/// Locates the <c>lspeek-backend</c> executable so a frontend can spawn its own private
/// backend instance. Resolution order: an explicit path, then the <c>LSPEEK_BACKEND</c>
/// environment variable, then the frontend's own output directory, then a bundled
/// <c>backend/</c> subdirectory next to the frontend (how packaged tools ship it), then the
/// in-repo dev build output (<c>src/Client.Backend/bin/&lt;config&gt;/&lt;tfm&gt;</c>).
/// </summary>
/// <remarks>
/// The dev-build probe is a convenience for working in this repo. Packaged tools bundle the
/// backend's framework-dependent publish output under a <c>backend/</c> folder beside the
/// frontend (see <c>src/Bundle.Backend.targets</c>); both that and <c>LSPEEK_BACKEND</c> are
/// found before the dev-build probe.
/// </remarks>
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
    public static (string FileName, IReadOnlyList<string> PrefixArgs) Resolve(string? explicitPath = null)
    {
        var configured = explicitPath ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured) && TryDirectory(configured) is { } fromDir)
                return fromDir;
            if (File.Exists(configured))
                return AsLaunch(configured);
            throw new BackendException(
                $"Configured backend path '{configured}' " +
                $"(from {(explicitPath is null ? EnvironmentVariable : "options")}) was not found.");
        }

        var baseDir = AppContext.BaseDirectory;
        if (TryDirectory(baseDir) is { } fromBase)
            return fromBase;

        // Packaged tools drop the backend's publish output in a "backend" folder beside the frontend.
        if (TryDirectory(Path.Combine(baseDir, BundledSubdirectory)) is { } fromBundle)
            return fromBundle;

        foreach (var dir in DevBuildDirectories(baseDir))
        {
            if (TryDirectory(dir) is { } fromDev)
                return fromDev;
        }

        throw new BackendException(
            $"Could not locate the lspeek backend ('{ExecutableName}'). Build src/Client.Backend, " +
            $"or set the {EnvironmentVariable} environment variable to the backend executable or its directory.");
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

    private static IEnumerable<string> DevBuildDirectories(string baseDir)
    {
        var trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // The leaf is usually the TFM (e.g. net10.0), but a self-contained/RID build nests the
        // apphost one level deeper (e.g. net10.0/win-x64), so locate the TFM segment explicitly.
        var tfm = FindTfmSegment(trimmed) ?? new DirectoryInfo(trimmed).Name;
        var config = baseDir.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        for (var dir = new DirectoryInfo(trimmed); dir is not null; dir = dir.Parent)
        {
            // A consumer building under src/<Project>/bin/... has an ancestor named "src".
            if (string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(dir.FullName, "Client.Backend", "bin", config, tfm);

            // Any consumer (incl. tests/) can resolve via the repo root holding the solution.
            if (File.Exists(Path.Combine(dir.FullName, "lspeek.slnx")))
            {
                yield return Path.Combine(dir.FullName, "src", "Client.Backend", "bin", config, tfm);
                yield break;
            }
        }
    }

    /// <summary>Finds the target-framework segment (e.g. <c>net10.0</c>) within a build output path.</summary>
    private static string? FindTfmSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Prefer the deepest matching segment so nested layouts resolve to the closest TFM.
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                && segment.Length > 3
                && char.IsDigit(segment[3]))
            {
                return segment;
            }
        }
        return null;
    }
}
