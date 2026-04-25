using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Tui.Interactive.Framework;
using Spectre.Console;

namespace ManualLspClient.Tui.Interactive.Views;

/// <summary>
/// Expanded view of a single session message, showing metadata and scrollable JSON body.
/// </summary>
public class MessageDetailView : ITuiView
{
    private readonly TuiStore _store;
    private int _messageIndex;
    private int _jsonScrollOffset;

    public MessageDetailView(TuiStore store)
    {
        _store = store;
    }

    public void OnEnter(object? args)
    {
        if (args is int index)
            _messageIndex = index;
        _jsonScrollOffset = 0;
    }

    public void Render(RenderContext ctx)
    {
        var messages = _store.GetMessages();
        if (_messageIndex < 0 || _messageIndex >= messages.Count)
        {
            ctx.WritePaddedLine("[dim]  Message not found[/]");
            ctx.ClearRemainingLines(1);
            return;
        }

        var msg = messages[_messageIndex];
        int linesRendered = msg.IsStderr
            ? RenderStderrDetail(msg, ctx)
            : RenderMessageDetail(msg, ctx);
        ctx.ClearRemainingLines(linesRendered);
    }

    public Task<Navigation> HandleKeyAsync(ConsoleKeyInfo key)
    {
        var messages = _store.GetMessages();

        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Enter:
                return Task.FromResult<Navigation>(new Navigation.Pop());

            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                _jsonScrollOffset++;
                break;

            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                if (_jsonScrollOffset > 0) _jsonScrollOffset--;
                break;

            case ConsoleKey.N:
                if (_messageIndex < messages.Count - 1)
                {
                    _messageIndex++;
                    _store.SelectedIndex = _messageIndex;
                    _jsonScrollOffset = 0;
                }
                break;

            case ConsoleKey.P:
                if (_messageIndex > 0)
                {
                    _messageIndex--;
                    _store.SelectedIndex = _messageIndex;
                    _jsonScrollOffset = 0;
                }
                break;

            case ConsoleKey.C:
                CopyJson(messages);
                break;

            case ConsoleKey.M:
                JumpToMatch(messages);
                break;

            case ConsoleKey.R:
            {
                if (_messageIndex < messages.Count)
                {
                    var msg = messages[_messageIndex];
                    if (msg.MessageType == MessageType.Request && msg.Direction == MessageDirection.Sent)
                    {
                        var paramsJson = msg.Json.HasValue ? msg.GetFormattedJson() : null;
                        return Task.FromResult<Navigation>(
                            new Navigation.Push(typeof(ParamsEditorView),
                                new ParamsEditorArgs(msg.Method, IsCustom: false, ExistingParams: paramsJson)));
                    }
                }
                break;
            }

            case ConsoleKey.X:
                ExportSession();
                break;

            case ConsoleKey.Q:
                return Task.FromResult<Navigation>(new Navigation.ExitApp());
        }

        return Task.FromResult<Navigation>(new Navigation.Stay());
    }

    public IReadOnlyList<FooterHint> GetFooterHints()
    {
        var hints = new List<FooterHint>
        {
            new("Esc", "Back"),
            new("↑/↓", "Scroll"),
            new("N/P", "Next/Prev"),
            new("C", "Copy JSON"),
        };

        var messages = _store.GetMessages();
        if (_messageIndex >= 0 && _messageIndex < messages.Count)
        {
            var msg = messages[_messageIndex];
            int? matchIndex = TuiStore.FindMatchingMessageIndex(msg, messages);

            if (matchIndex.HasValue)
            {
                var matchLabel = msg.MessageType == MessageType.Request ? "Go to Response" : "Go to Request";
                hints.Add(new("M", matchLabel));
            }

            if (msg.MessageType == MessageType.Request && msg.Direction == MessageDirection.Sent)
            {
                hints.Add(new("R", "Resend"));
            }
        }

        hints.Add(new("Q", "Quit"));
        return hints;
    }

    // ── Render helpers ──

    private int RenderMessageDetail(SessionMessage msg, RenderContext ctx)
    {
        int linesRendered = 0;
        var contentWidth = Math.Max(1, ctx.TermWidth - 2);

        var arrow = msg.Direction == MessageDirection.Sent ? "Sent" : "Received";
        var typeLabel = msg.MessageType.ToString();
        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var statusColor = msg.GetStatusColor();

        ctx.WritePaddedLine($"  [bold]Message Details[/]  [dim](index {_messageIndex})[/]");
        linesRendered++;
        ctx.WritePaddedLine($"[grey]{new string('─', ctx.TermWidth - 1)}[/]");
        linesRendered++;

        ctx.WritePaddedLine($"  [bold]Time:[/] {time}  [bold]Direction:[/] {arrow}  [bold]Type:[/] {typeLabel}  [bold]Status:[/] [{statusColor}]{msg.GetStatusLabel()}[/]");
        linesRendered++;
        var idLabel = msg.Id.HasValue ? $"  [bold]Id:[/] {msg.Id.Value}" : "";
        ctx.WritePaddedLine($"  [bold]Method:[/] {Markup.Escape(msg.Method)}{idLabel}");
        linesRendered++;

        if (msg.Json.HasValue)
        {
            var label = msg.MessageType == MessageType.Response ? "Result" : "Params";
            var formatted = msg.GetFormattedJson();
            var allLines = GetWrappedLines(formatted.ReplaceLineEndings("\n").Split('\n'), contentWidth);
            var jsonAvailable = Math.Max(3, ctx.AvailableLines - linesRendered - 1);

            var maxOffset = Math.Max(0, allLines.Count - jsonAvailable);
            _jsonScrollOffset = Math.Clamp(_jsonScrollOffset, 0, maxOffset);

            var scrollInfo = allLines.Count > jsonAvailable
                ? $" [dim]({_jsonScrollOffset + 1}–{Math.Min(_jsonScrollOffset + jsonAvailable, allLines.Count)} of {allLines.Count})[/]"
                : "";

            ctx.WritePaddedLine($"  [bold]{label}[/]{scrollInfo}");
            linesRendered++;

            var visibleLines = allLines.Skip(_jsonScrollOffset).Take(jsonAvailable);
            foreach (var line in visibleLines)
            {
                ctx.WritePaddedLine($"  [grey]{Markup.Escape(line)}[/]");
                linesRendered++;
            }
        }

        return linesRendered;
    }

    private int RenderStderrDetail(SessionMessage msg, RenderContext ctx)
    {
        int linesRendered = 0;
        var contentWidth = Math.Max(1, ctx.TermWidth - 2);

        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

        ctx.WritePaddedLine($"  [bold red]Server stderr[/]  [dim](index {_messageIndex})[/]");
        linesRendered++;
        ctx.WritePaddedLine($"[red]{new string('─', ctx.TermWidth - 1)}[/]");
        linesRendered++;
        ctx.WritePaddedLine($"  [bold]Time:[/] {time}  [bold]Lines:[/] {msg.StderrLines.Count}");
        linesRendered++;

        var allLines = GetWrappedLines(msg.StderrLines, contentWidth);
        var stderrAvailable = Math.Max(3, ctx.AvailableLines - linesRendered - 1);
        var maxOffset = Math.Max(0, allLines.Count - stderrAvailable);
        _jsonScrollOffset = Math.Clamp(_jsonScrollOffset, 0, maxOffset);

        var scrollInfo = allLines.Count > stderrAvailable
            ? $" [dim]({_jsonScrollOffset + 1}–{Math.Min(_jsonScrollOffset + stderrAvailable, allLines.Count)} of {allLines.Count})[/]"
            : "";

        ctx.WritePaddedLine($"  [bold red]Output[/]{scrollInfo}");
        linesRendered++;

        var visibleLines = allLines.Skip(_jsonScrollOffset).Take(stderrAvailable);
        foreach (var line in visibleLines)
        {
            ctx.WritePaddedLine($"  [red]{Markup.Escape(line)}[/]");
            linesRendered++;
        }

        return linesRendered;
    }

    private static IReadOnlyList<string> GetWrappedLines(IEnumerable<string> lines, int contentWidth)
    {
        var wrapped = new List<string>();
        foreach (var line in lines)
        {
            wrapped.AddRange(RenderContext.WrapLine(line, contentWidth));
        }

        return wrapped;
    }

    // ── Actions ──

    private void CopyJson(IReadOnlyList<SessionMessage> messages)
    {
        if (_messageIndex < 0 || _messageIndex >= messages.Count) return;
        var msg = messages[_messageIndex];

        string? text = null;
        if (msg.IsStderr)
            text = string.Join('\n', msg.StderrLines);
        else if (msg.Json.HasValue)
            text = msg.GetFormattedJson();

        if (text is not null)
            TuiStore.CopyToClipboard(text);
    }

    private void JumpToMatch(IReadOnlyList<SessionMessage> messages)
    {
        if (_messageIndex < 0 || _messageIndex >= messages.Count) return;
        var matchIndex = TuiStore.FindMatchingMessageIndex(messages[_messageIndex], messages);
        if (matchIndex.HasValue)
        {
            _messageIndex = matchIndex.Value;
            _store.SelectedIndex = matchIndex.Value;
            _jsonScrollOffset = 0;
        }
    }

    private void ExportSession()
    {
        AnsiConsole.Clear();

        var exportType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Export type:[/]")
                .AddChoices("Full session log (all messages)", "Script export (sent only)"));

        var defaultFile = exportType.StartsWith("Full")
            ? "session-log.json"
            : "session-export.json";

        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Export path:[/]")
                .DefaultValue(defaultFile));

        if (exportType.StartsWith("Full"))
            _store.ExportFullSessionLog(path);
        else
            _store.ExportSession(path);

        AnsiConsole.MarkupLine($"[green]✓ Exported to {Markup.Escape(path)}[/]");
        Thread.Sleep(1000);
    }
}
