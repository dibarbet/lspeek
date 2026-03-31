using ManualLspClient.Core.MetaModel;
using Spectre.Console;

namespace ManualLspClient.Tui.Interactive;

/// <summary>
/// Builds LSP request/notification params interactively using the metamodel for template generation.
/// </summary>
public class RequestBuilder
{
    private readonly LspMetaModelProvider _metaModel;
    private readonly RequestTemplateGenerator _templateGenerator;

    public RequestBuilder(LspMetaModelProvider metaModel)
    {
        _metaModel = metaModel;
        _templateGenerator = new RequestTemplateGenerator(metaModel);
    }

    /// <summary>
    /// Interactively builds a request. Returns (method, paramsJson) or (null, null) if cancelled.
    /// </summary>
    public (string? method, string? paramsJson) BuildRequest()
    {
        var requests = _metaModel.GetClientRequests();
        var methods = requests.Select(r => r.Method).ToList();
        return BuildMessage(methods, "request");
    }

    /// <summary>
    /// Interactively builds a notification. Returns (method, paramsJson) or (null, null) if cancelled.
    /// </summary>
    public (string? method, string? paramsJson) BuildNotification()
    {
        var notifications = _metaModel.GetClientNotifications();
        var methods = notifications.Select(n => n.Method).ToList();
        return BuildMessage(methods, "notification");
    }

    private (string? method, string? paramsJson) BuildMessage(List<string> methods, string messageType)
    {
        // Group methods by category for easier navigation
        var grouped = RequestTemplateGenerator.GroupMethodsByCategory(methods);

        // Build selection prompt with grouped methods
        var prompt = new SelectionPrompt<string>()
            .Title($"[bold]Select {messageType} method:[/]")
            .PageSize(20)
            .MoreChoicesText("[grey](Move up and down to see more methods)[/]");

        prompt.AddChoice("← Cancel");
        prompt.AddChoice("Custom method...");

        foreach (var (category, categoryMethods) in grouped)
        {
            prompt.AddChoiceGroup(category, categoryMethods);
        }

        var selected = AnsiConsole.Prompt(prompt);

        if (selected == "← Cancel")
            return (null, null);

        string method;
        if (selected == "Custom method...")
        {
            method = AnsiConsole.Prompt(new TextPrompt<string>("[bold]Method name:[/]"));
        }
        else
        {
            method = selected;
        }

        // Generate template from metamodel
        var template = _templateGenerator.GenerateTemplate(method);

        string? paramsJson;
        if (template is not null)
        {
            AnsiConsole.MarkupLine($"[dim]Template generated from LSP metamodel. Edit the JSON below:[/]");
            AnsiConsole.Write(new Panel(Markup.Escape(template))
                .Header("[blue]Template[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Blue));

            paramsJson = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Params JSON[/] (press Enter to use template, or type new JSON):")
                    .DefaultValue(template)
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(paramsJson))
                paramsJson = template;
        }
        else
        {
            paramsJson = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Params JSON[/] (or empty for none):")
                    .AllowEmpty());
        }

        if (string.IsNullOrWhiteSpace(paramsJson))
            paramsJson = null;

        return (method, paramsJson);
    }
}
