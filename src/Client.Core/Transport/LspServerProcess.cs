using System.Diagnostics;

namespace ManualLspClient.Core.Transport;

/// <summary>
/// Manages the lifecycle of an LSP server process.
/// </summary>
public class LspServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<string> _stderrLines = [];
    private readonly object _stderrLock = new();
    private bool _disposed;

    public Stream InputStream => _process.StandardInput.BaseStream;
    public Stream OutputStream => _process.StandardOutput.BaseStream;
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    /// <summary>The process exit code, or <c>null</c> if it is still running.</summary>
    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Wait for the process to exit (best-effort; returns immediately if already gone).</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.HasExited ? Task.CompletedTask : _process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Event raised when a line is written to the server's stderr.
    /// </summary>
    public event Action<string>? StderrLineReceived;

    /// <summary>
    /// Event raised when the server process exits.
    /// </summary>
    public event Action? Exited;

    private LspServerProcess(Process process)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public static LspServerProcess Start(Configuration.ServerConfig config)
    {
        var resolvedCommand = ResolveCommand(config.Command);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var arg in config.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (config.Environment is not null)
        {
            foreach (var (key, value) in config.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start server process: {config.Command} {string.Join(" ", config.Arguments)}");

        var serverProcess = new LspServerProcess(process);
        serverProcess.StartStderrCapture();
        return serverProcess;
    }

    public IReadOnlyList<string> GetStderrLines()
    {
        lock (_stderrLock)
        {
            return [.. _stderrLines];
        }
    }

    private void StartStderrCapture()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_stderrLock)
                    {
                        _stderrLines.Add(line);
                    }
                    StderrLineReceived?.Invoke(line);
                }
            }
            catch (ObjectDisposedException)
            {
                // Process was disposed, stop reading
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort
            }
        }

        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resolves a command name to a full path, handling Windows .cmd/.bat/.exe extensions.
    /// </summary>
    private static string ResolveCommand(string command)
    {
        // If it already has a full path or extension, use as-is
        if (File.Exists(command))
            return command;

        // On Windows, search PATH for the command with common extensions
        if (!OperatingSystem.IsWindows())
            return command;

        var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            foreach (var ext in pathExtensions)
            {
                var fullPath = Path.Combine(dir, command + ext);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        // Fall back to the original command and let Process.Start handle the error
        return command;
    }
}
