using ManualLspClient.Core.Configuration;
using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Tui.Interactive;
using ManualLspClient.Tui.Scripting;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

namespace ManualLspClient.Tui.Commands;

public class ConnectCommand : AsyncCommand<ConnectCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<server>")]
        [Description("Built-in server name (e.g. 'roslyn') or path to a server config JSON file")]
        public string Server { get; set; } = "";

        [CommandOption("--no-init")]
        [Description("Skip automatic initialize/initialized handshake")]
        public bool NoInit { get; set; }

        [CommandOption("--json <PATH>")]
        [Description("Path to a JSON script file to run before entering interactive mode")]
        public string? JsonPath { get; set; }

        [CommandOption("--exit")]
        [Description("Exit after script completes (only with --json)")]
        public bool Exit { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Resolve server configuration
        var configProvider = ServerConfigProvider.Load();
        ServerConfig serverConfig;
        try
        {
            serverConfig = configProvider.Resolve(settings.Server);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold blue]Starting LSP server:[/] {serverConfig.Name}");
        AnsiConsole.MarkupLine($"[dim]Command:[/] {serverConfig.Command} {string.Join(" ", serverConfig.Arguments)}");

        // Start the LSP session
        LspSession session;
        try
        {
            session = LspSession.Start(serverConfig);
            AnsiConsole.MarkupLine($"[green]Server started[/] (PID: {session.ServerProcessId})");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start server:[/] {ex.Message}");
            return 1;
        }

        await using (session)
        {
            // Auto-initialize unless --no-init
            if (!settings.NoInit)
            {
                try
                {
                    AnsiConsole.MarkupLine("[dim]Sending initialize...[/]");
                    var result = await session.InitializeAsync();
                    AnsiConsole.MarkupLine("[green]Initialize succeeded[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Initialize failed:[/] {ex.Message}");
                }
            }

            // Run JSON script if provided
            if (settings.JsonPath is not null)
            {
                var runner = new ScriptRunner(session);
                try
                {
                    await runner.RunAsync(settings.JsonPath);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Script error:[/] {ex.Message}");
                    if (settings.Exit) return 1;
                }

                if (settings.Exit)
                {
                    await session.ShutdownAsync();
                    return 0;
                }
            }

            // Enter interactive TUI
            var metaModel = LspMetaModelProvider.Load();
            var tui = new TuiSession(session, metaModel);
            await tui.RunAsync();
        }

        return 0;
    }
}
