using ManualLspClient.Core.Configuration;
using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Tui.Interactive.Framework;
using ManualLspClient.Tui.Interactive.Views;
using ManualLspClient.Tui.Scripting;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

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
        var isScriptMode = settings.JsonPath is not null;

        // Resolve server configuration
        var configProvider = ServerConfigProvider.Load();
        ServerConfig serverConfig;
        try
        {
            serverConfig = configProvider.Resolve(settings.Server);
        }
        catch (ArgumentException ex)
        {
            WriteError(isScriptMode, ex.Message);
            return 1;
        }

        WriteStatus(
            isScriptMode,
            $"Starting LSP server: {serverConfig.Name}",
            $"[bold blue]Starting LSP server:[/] {Markup.Escape(serverConfig.Name)}");
        WriteStatus(
            isScriptMode,
            $"Command: {serverConfig.Command} {string.Join(" ", serverConfig.Arguments)}",
            $"[dim]Command:[/] {Markup.Escape(serverConfig.Command)} {Markup.Escape(string.Join(" ", serverConfig.Arguments))}");

        // Start the LSP session
        LspSession session;
        try
        {
            session = LspSession.Start(serverConfig);
            WriteStatus(
                isScriptMode,
                $"Server started (PID: {session.ServerProcessId})",
                $"[green]Server started[/] (PID: {session.ServerProcessId})");
        }
        catch (Exception ex)
        {
            WriteError(isScriptMode, $"Failed to start server: {ex.Message}");
            return 1;
        }

        await using (session)
        {
            var hasScript = settings.JsonPath is not null;

            // Run JSON script if provided
            if (settings.JsonPath is not null)
            {
                var script = ScriptFile.Load(settings.JsonPath);
                var runner = new ScriptRunner(session);
                try
                {
                    await runner.RunAsync(script, settings.JsonPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    WriteError(true, $"Script error: {ex.Message}");
                    if (settings.Exit) return 1;
                }

                if (settings.Exit)
                {
                    if (!script.EndsWithShutdownAndExit())
                    {
                        if (script.EndsWithShutdownRequest())
                        {
                            await session.SendNotificationAsync("exit", null, cancellationToken);
                        }
                        else if (!script.EndsWithExitNotification())
                        {
                            await session.ShutdownAsync(cancellationToken);
                        }
                    }

                    return 0;
                }
            }

            // Enter interactive TUI
            var metaModel = LspMetaModelProvider.Load();
            var store = new TuiStore(session, metaModel)
            {
                AutoInit = !hasScript && !settings.NoInit && !session.IsInitialized
            };
            var host = new TuiHost(store)
                .RegisterView(() => new MessageListView(store))
                .RegisterView(() => new MessageDetailView(store))
                .RegisterView(() => new MethodPickerView(store))
                .RegisterView(() => new ParamsEditorView(store));
            await host.RunAsync<MessageListView>();
        }

        return 0;
    }

    private static void WriteStatus(bool scriptMode, string plainText, string markupText)
    {
        if (scriptMode)
        {
            Console.Error.WriteLine(plainText);
            return;
        }

        AnsiConsole.MarkupLine(markupText);
    }

    private static void WriteError(bool scriptMode, string message)
    {
        if (scriptMode)
        {
            Console.Error.WriteLine(message);
            return;
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }
}
