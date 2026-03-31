using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using Spectre.Console;

namespace ManualLspClient.Tui.Interactive;

/// <summary>
/// Renders the session log as a Spectre.Console Tree with expandable message details.
/// </summary>
public static class SessionLogView
{
    public static void Display(SessionLog log)
    {
        var messages = log.GetAll();

        if (messages.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No messages in session log.[/]");
            return;
        }

        // Ask for filter
        var filter = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Filter messages:[/]")
                .AddChoices("All", "Sent only", "Received only", "By method"));

        IReadOnlyList<SessionMessage> filtered = filter switch
        {
            "Sent only" => log.GetFiltered(direction: MessageDirection.Sent),
            "Received only" => log.GetFiltered(direction: MessageDirection.Received),
            "By method" => FilterByMethod(log),
            _ => messages
        };

        if (filtered.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No messages match the filter.[/]");
            return;
        }

        // Build tree
        var tree = new Tree($"[bold]Session Log[/] ({filtered.Count} messages)");

        foreach (var msg in filtered)
        {
            var directionIcon = msg.Direction == MessageDirection.Sent ? "→" : "←";
            var directionColor = msg.Direction == MessageDirection.Sent ? "cyan" : "yellow";
            var typeLabel = msg.MessageType.ToString().ToLowerInvariant();
            var idLabel = msg.Id.HasValue ? $" (id: {msg.Id})" : "";
            var timestamp = msg.Timestamp.ToString("HH:mm:ss.fff");

            var nodeLabel = $"[dim]{timestamp}[/] [{directionColor}]{directionIcon}[/] [bold]{Markup.Escape(msg.Method)}[/] [dim]{typeLabel}{idLabel}[/]";
            var node = tree.AddNode(nodeLabel);

            if (msg.Json.HasValue)
            {
                var formatted = msg.GetFormattedJson();
                if (formatted.Length > 1000)
                    formatted = formatted[..1000] + "\n...";
                node.AddNode(new Panel(Markup.Escape(formatted))
                    .Border(BoxBorder.Rounded)
                    .BorderColor(msg.Direction == MessageDirection.Sent ? Color.Cyan1 : Color.Yellow));
            }
        }

        AnsiConsole.Write(tree);

        // Optionally expand a specific message
        AnsiConsole.MarkupLine("\n[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static IReadOnlyList<SessionMessage> FilterByMethod(SessionLog log)
    {
        var allMethods = log.GetAll()
            .Select(m => m.Method)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        if (allMethods.Count == 0)
            return [];

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select method:[/]")
                .AddChoices(allMethods));

        return log.GetFiltered(method: selected);
    }
}
