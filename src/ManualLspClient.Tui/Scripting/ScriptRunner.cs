using ManualLspClient.Core.Session;
using Spectre.Console;
using System.Text.Json;

namespace ManualLspClient.Tui.Scripting;

/// <summary>
/// Executes a JSON script file against an LSP session, sending messages sequentially.
/// </summary>
public class ScriptRunner
{
    private readonly LspSession _session;

    public ScriptRunner(LspSession session)
    {
        _session = session;
    }

    public async Task RunAsync(string scriptPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Script file not found: {scriptPath}");

        var script = ScriptFile.Load(scriptPath);
        AnsiConsole.MarkupLine($"[bold]Running script:[/] {Markup.Escape(scriptPath)} ({script.Entries.Count} entries)");

        for (int i = 0; i < script.Entries.Count; i++)
        {
            var entry = script.Entries[i];
            AnsiConsole.MarkupLine($"[dim][{i + 1}/{script.Entries.Count}] {entry.Type}: {Markup.Escape(entry.Method)}[/]");

            if (entry.Type.Equals("notification", StringComparison.OrdinalIgnoreCase))
            {
                await _session.SendNotificationAsync(entry.Method, entry.Params, cancellationToken);

                // Print to stdout for piping
                var output = new { method = entry.Method, type = "notification", status = "sent" };
                Console.WriteLine(JsonSerializer.Serialize(output));
            }
            else // "request" is the default
            {
                var result = await _session.SendRequestAsync(entry.Method, entry.Params, cancellationToken);

                // Print response to stdout for piping
                var output = new { method = entry.Method, type = "request", result };
                Console.WriteLine(JsonSerializer.Serialize(output));
            }
        }

        AnsiConsole.MarkupLine($"[green]Script completed ({script.Entries.Count} entries)[/]");
    }
}
