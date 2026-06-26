using Lspeek.Core.Configuration;
using Lspeek.Tui.MetaModel;
using Lspeek.Protocol;
using Lspeek.Tui.Interactive.Framework;
using Lspeek.Tui.Interactive.Views;
using Lspeek.Tui.Scripting;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Lspeek.Tui.Commands;

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

        // Spawn the private backend that owns the LSP server.
        await using var client = new BackendClient(new BackendClientOptions
        {
            OnStderr = isScriptMode ? (line => Console.Error.WriteLine(line)) : null,
        });
        try
        {
            await client.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            WriteError(isScriptMode, $"Failed to start backend: {ex.Message}");
            return 1;
        }

        // Start the LSP server inside the backend.
        try
        {
            var startResponse = await client.StartServerAsync(new StartServerRequest { Server = settings.Server }, cancellationToken);
            WriteStatus(
                isScriptMode,
                $"Server started (PID: {startResponse.Pid})",
                $"[green]Server started[/] (PID: {startResponse.Pid})");
        }
        catch (Exception ex)
        {
            WriteError(isScriptMode, $"Failed to start server: {ex.Message}");
            return 1;
        }

        var hasScript = settings.JsonPath is not null;

        if (settings.JsonPath is not null)
        {
            var script = ScriptFile.Load(settings.JsonPath);
            var runner = new ScriptRunner(client);
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
                        await client.NotifyAsync("exit", null, cancellationToken);
                    }
                    else if (!script.EndsWithExitNotification())
                    {
                        await client.StopServerAsync(cancellationToken);
                    }
                }

                return 0;
            }
        }

        // Enter interactive TUI
        var metaModel = LspMetaModelProvider.Load();
        ServerStatus? initialStatus = null;
        try
        {
            initialStatus = await client.GetStatusAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Status is best-effort; the SSE backfill will populate it.
        }

        var store = new TuiStore(client, metaModel, serverConfig.Name, initialStatus)
        {
            AutoInit = !hasScript && !settings.NoInit,
        };
        await store.StartAsync();

        var host = new TuiHost(store)
            .RegisterView(() => new MessageListView(store))
            .RegisterView(() => new MessageDetailView(store))
            .RegisterView(() => new MethodPickerView(store))
            .RegisterView(() => new ParamsEditorView(store));
        await host.RunAsync<MessageListView>();

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
