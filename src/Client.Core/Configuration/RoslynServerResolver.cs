using System.Text.Json;
using Lspeek.Protocol;

namespace Lspeek.Core.Configuration;

/// <summary>
/// Resolves a locally-built Roslyn language server (<c>Microsoft.CodeAnalysis.LanguageServer.dll</c>)
/// from a repo root/worktree, output directory, or full dll path, and translates friendly
/// <see cref="StartServerRequest"/> options into a launchable <see cref="ServerConfig"/>.
/// </summary>
public static class RoslynServerResolver
{
    private const string ServerDllName = "Microsoft.CodeAnalysis.LanguageServer.dll";

    /// <summary>True if the request targets the Roslyn build resolver (vs a named/config server).</summary>
    public static bool IsRoslynRequest(StartServerRequest request)
        => !string.IsNullOrWhiteSpace(request.ServerPath) || !string.IsNullOrWhiteSpace(request.RepoRoot);

    /// <summary>
    /// Build a <see cref="ServerConfig"/> for a Roslyn server, resolving the dll and assembling
    /// the <c>dotnet &lt;dll&gt; --stdio ...</c> command line.
    /// </summary>
    public static ServerConfig BuildConfig(StartServerRequest request, out IReadOnlyList<ServerCandidate> candidates)
    {
        var (dllPath, found) = Resolve(request.ServerPath, request.RepoRoot, request.Configuration);
        candidates = found;

        var arguments = new List<string> { dllPath, "--stdio" };
        arguments.AddRange(BuildServerArgs(request));

        var environment = request.Env is { Count: > 0 }
            ? new Dictionary<string, string>(request.Env)
            : null;

        return new ServerConfig
        {
            Name = "roslyn",
            Command = string.IsNullOrWhiteSpace(request.DotnetPath) ? "dotnet" : request.DotnetPath!,
            Arguments = [.. arguments],
            WorkingDirectory = string.IsNullOrWhiteSpace(request.Cwd) ? Path.GetDirectoryName(dllPath) : request.Cwd,
            Environment = environment,
        };
    }

    /// <summary>
    /// Resolve the path to the built language-server dll, returning the chosen dll and the full
    /// list of candidate builds (newest first) when a search was performed.
    /// </summary>
    public static (string DllPath, IReadOnlyList<ServerCandidate> Candidates) Resolve(
        string? serverPath, string? repoRoot, string? configuration)
    {
        // A direct path to the dll.
        if (!string.IsNullOrWhiteSpace(serverPath) && serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(serverPath))
                throw new FileNotFoundException($"Server dll not found at '{serverPath}'.");
            return (Path.GetFullPath(serverPath), []);
        }

        var bases = new List<string>();
        void PushBase(string? p)
        {
            if (!string.IsNullOrEmpty(p) && !bases.Contains(p))
                bases.Add(p);
        }

        foreach (var candidate in new[] { serverPath, repoRoot })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var resolved = Path.GetFullPath(candidate);
            if (!Directory.Exists(resolved) && !File.Exists(resolved))
                continue;

            // The directory itself may contain the dll (a build/publish output dir).
            var directDll = Path.Combine(resolved, ServerDllName);
            if (File.Exists(directDll))
                return (directDll, []);

            // The directory may be the .../artifacts/bin/Microsoft.CodeAnalysis.LanguageServer dir.
            if (Path.GetFileName(resolved) == "Microsoft.CodeAnalysis.LanguageServer")
                PushBase(Path.GetDirectoryName(Path.GetDirectoryName(resolved))); // -> repo root
            PushBase(resolved);
        }

        if (bases.Count == 0)
        {
            throw new InvalidOperationException(
                "No serverPath or repoRoot provided (or they do not exist). Provide a path to " +
                $"{ServerDllName}, to its output directory, or to the repo root / worktree.");
        }

        var candidates = new List<(ServerCandidate Candidate, DateTime Mtime)>();
        foreach (var baseDir in bases)
        {
            var binDir = Path.Combine(baseDir, "artifacts", "bin", "Microsoft.CodeAnalysis.LanguageServer");
            if (!Directory.Exists(binDir))
                continue;

            // Layout: <binDir>/<Configuration>/<tfm>/Microsoft.CodeAnalysis.LanguageServer.dll
            foreach (var configDir in SafeDirs(binDir))
            {
                foreach (var tfmDir in SafeDirs(configDir))
                {
                    var dll = Path.Combine(tfmDir, ServerDllName);
                    if (!File.Exists(dll))
                        continue;
                    candidates.Add((new ServerCandidate
                    {
                        Path = dll,
                        Configuration = Path.GetFileName(configDir),
                        Tfm = Path.GetFileName(tfmDir),
                    }, File.GetLastWriteTimeUtc(dll)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Could not find {ServerDllName} under artifacts/bin/Microsoft.CodeAnalysis.LanguageServer " +
                $"for any of: {string.Join(", ", bases)}. Build the server first " +
                "(e.g. `dotnet build src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer`).");
        }

        IEnumerable<(ServerCandidate Candidate, DateTime Mtime)> filtered = candidates;
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            var wanted = candidates
                .Where(c => string.Equals(c.Candidate.Configuration, configuration, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (wanted.Count > 0)
                filtered = wanted;
        }

        var ordered = filtered.OrderByDescending(c => c.Mtime).Select(c => c.Candidate).ToList();
        return (ordered[0].Path, ordered);
    }

    /// <summary>Translate friendly options into Roslyn server CLI args (appended after <c>--stdio</c>).</summary>
    public static List<string> BuildServerArgs(StartServerRequest request)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.LogLevel))
            args.AddRange(["--logLevel", request.LogLevel!]);

        if (!string.IsNullOrWhiteSpace(request.ExtensionLogDirectory))
            args.AddRange(["--extensionLogDirectory", request.ExtensionLogDirectory!]);

        if (request.AutoLoadProjects is { } alp && alp.ValueKind != JsonValueKind.Null)
        {
            var isFalse = alp.ValueKind == JsonValueKind.False;
            if (!isFalse)
            {
                args.Add("--autoLoadProjects");
                if (alp.ValueKind == JsonValueKind.Number)
                    args.Add(alp.GetRawText());
            }
        }

        foreach (var ext in request.Extensions ?? [])
            args.AddRange(["--extension", ext]);

        if (request.ExtraArgs is { Count: > 0 })
            args.AddRange(request.ExtraArgs);

        return args;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return [];
        }
    }
}
