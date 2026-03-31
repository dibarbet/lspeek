using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Tui.Scripting;
using Spectre.Console;
using System.Text.Json;

namespace ManualLspClient.Tui.Interactive;

/// <summary>
/// Main interactive TUI loop using Spectre.Console.
/// </summary>
public class TuiSession
{
    private readonly LspSession _session;
    private readonly LspMetaModelProvider _metaModel;
    private readonly RequestBuilder _requestBuilder;
    private readonly CancellationTokenSource _cts = new();

    public TuiSession(LspSession session, LspMetaModelProvider metaModel)
    {
        _session = session;
        _metaModel = metaModel;
        _requestBuilder = new RequestBuilder(metaModel);

        // Show server notifications inline
        _session.NotificationReceived += (method, json) =>
        {
            AnsiConsole.MarkupLine($"\n[dim yellow]← Server notification:[/] [yellow]{Markup.Escape(method)}[/]");
            if (json.HasValue)
            {
                var formatted = JsonSerializer.Serialize(json.Value, new JsonSerializerOptions { WriteIndented = true });
                if (formatted.Length > 500)
                    formatted = formatted[..500] + "\n...";
                AnsiConsole.Write(new Panel(Markup.Escape(formatted))
                    .Header($"[yellow]{Markup.Escape(method)}[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
            }
        };
    }

    public async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[bold]Interactive LSP session started.[/] Select an action below.\n");

        while (!_cts.Token.IsCancellationRequested && _session.IsServerRunning)
        {
            DisplayStatus();

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Choose an action:[/]")
                    .AddChoices(
                        "Send Request",
                        "Send Notification",
                        "Send Raw JSON",
                        "View Session Log",
                        "Export Session",
                        "View Server Stderr",
                        "Shutdown & Exit"));

            try
            {
                switch (action)
                {
                    case "Send Request":
                        await SendRequestAsync();
                        break;
                    case "Send Notification":
                        await SendNotificationAsync();
                        break;
                    case "Send Raw JSON":
                        await SendRawJsonAsync();
                        break;
                    case "View Session Log":
                        SessionLogView.Display(_session.Log);
                        break;
                    case "Export Session":
                        ExportSession();
                        break;
                    case "View Server Stderr":
                        ViewServerStderr();
                        break;
                    case "Shutdown & Exit":
                        await _session.ShutdownAsync();
                        _cts.Cancel();
                        AnsiConsole.MarkupLine("[green]Session ended.[/]");
                        break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            }

            AnsiConsole.WriteLine();
        }
    }

    private void DisplayStatus()
    {
        var status = _session.IsServerRunning ? "[green]Running[/]" : "[red]Stopped[/]";
        var init = _session.IsInitialized ? "[green]Yes[/]" : "[yellow]No[/]";
        var table = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
        table.AddRow("Server", $"[bold]{Markup.Escape(_session.ServerConfig.Name)}[/] (PID: {_session.ServerProcessId}) {status}");
        table.AddRow("Initialized", init);
        table.AddRow("Messages", _session.Log.Count.ToString());
        AnsiConsole.Write(table);
    }

    private async Task SendRequestAsync()
    {
        var (method, paramsJson) = _requestBuilder.BuildRequest();
        if (method is null) return;

        AnsiConsole.MarkupLine($"[dim]→ Sending request: {Markup.Escape(method)}[/]");

        var paramsObj = paramsJson is not null
            ? JsonDocument.Parse(paramsJson).RootElement
            : (object?)null;

        var result = await _session.SendRequestAsync(method, paramsObj, _cts.Token);

        var formatted = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        AnsiConsole.Write(new Panel(Markup.Escape(formatted))
            .Header($"[green]Response: {Markup.Escape(method)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green));
    }

    private async Task SendNotificationAsync()
    {
        var (method, paramsJson) = _requestBuilder.BuildNotification();
        if (method is null) return;

        AnsiConsole.MarkupLine($"[dim]→ Sending notification: {Markup.Escape(method)}[/]");

        var paramsObj = paramsJson is not null
            ? JsonDocument.Parse(paramsJson).RootElement
            : (object?)null;

        await _session.SendNotificationAsync(method, paramsObj, _cts.Token);
        AnsiConsole.MarkupLine("[green]Notification sent[/]");
    }

    private async Task SendRawJsonAsync()
    {
        AnsiConsole.MarkupLine("[dim]Enter the LSP method name:[/]");
        var method = AnsiConsole.Prompt(new TextPrompt<string>("[bold]Method:[/]"));

        AnsiConsole.MarkupLine("[dim]Enter JSON params (or empty for none):[/]");
        var paramsJson = AnsiConsole.Prompt(new TextPrompt<string>("[bold]Params JSON:[/]").AllowEmpty());

        var isNotification = AnsiConsole.Confirm("Is this a notification (no response expected)?", false);

        object? paramsObj = string.IsNullOrWhiteSpace(paramsJson)
            ? null
            : JsonDocument.Parse(paramsJson).RootElement;

        if (isNotification)
        {
            await _session.SendNotificationAsync(method, paramsObj, _cts.Token);
            AnsiConsole.MarkupLine("[green]Notification sent[/]");
        }
        else
        {
            var result = await _session.SendRequestAsync(method, paramsObj, _cts.Token);
            var formatted = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            AnsiConsole.Write(new Panel(Markup.Escape(formatted))
                .Header($"[green]Response: {Markup.Escape(method)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Green));
        }
    }

    private void ExportSession()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Export path:[/]")
                .DefaultValue("session-export.json"));

        ScriptExporter.Export(_session.Log, path);
        AnsiConsole.MarkupLine($"[green]Session exported to:[/] {Markup.Escape(path)}");
    }

    private void ViewServerStderr()
    {
        var lines = _session.GetServerStderr();
        if (lines.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No stderr output from server.[/]");
            return;
        }

        var panel = new Panel(Markup.Escape(string.Join("\n", lines)))
            .Header("[red]Server Stderr[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red);
        AnsiConsole.Write(panel);
    }
}
