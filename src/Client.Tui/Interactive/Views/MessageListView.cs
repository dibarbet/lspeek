using Lspeek.Tui.Presentation;
using Lspeek.Tui.Session;
using Lspeek.Tui.Interactive.Framework;
using Spectre.Console;
using System.Text;

namespace Lspeek.Tui.Interactive.Views;

/// <summary>
/// Collapsed session log view — shows one-line summaries of all messages
/// with selection cursor and scrolling.
/// </summary>
public class MessageListView : ITuiView
{
    private readonly TuiStore _store;

    public MessageListView(TuiStore store)
    {
        _store = store;
    }

    public void OnEnter(object? args)
    {
        // Ensure selection is valid when returning to this view
        _store.FollowLatestMessageIfPinned();
    }

    public void Render(RenderContext ctx)
    {
        _store.FollowLatestMessageIfPinned();
        var messages = _store.GetMessages();
        int linesRendered = RenderCollapsedLog(messages, ctx);
        ctx.ClearRemainingLines(linesRendered);
    }

    public Task<Navigation> HandleKeyAsync(ConsoleKeyInfo key)
    {
        var messages = _store.GetMessages();

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                if (_store.SelectedIndex > 0) _store.SelectedIndex--;
                break;

            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                if (_store.SelectedIndex < messages.Count - 1) _store.SelectedIndex++;
                break;

            case ConsoleKey.Enter:
                if (messages.Count > 0 && _store.SelectedIndex >= 0)
                    return Task.FromResult<Navigation>(
                        new Navigation.Push(typeof(MessageDetailView), _store.SelectedIndex));
                break;

            case ConsoleKey.R:
                if (!_store.IsServerRunning)
                {
                    break;
                }
                return Task.FromResult<Navigation>(
                    new Navigation.Push(typeof(MethodPickerView)));

            case ConsoleKey.X:
                ExportSession();
                break;

            case ConsoleKey.Q:
                return Task.FromResult<Navigation>(new Navigation.ExitApp());
        }

        return Task.FromResult<Navigation>(new Navigation.Stay());
    }

    public IReadOnlyList<FooterHint> GetFooterHints() =>
    [
        new("Enter", "Expand"),
        new("↑/↓", "Move"),
        new("R", "Send Request"),
        new("X", "Export"),
        new("Q", "Quit"),
    ];

    private int RenderCollapsedLog(IReadOnlyList<SessionMessage> messages, RenderContext ctx)
    {
        int linesRendered = 0;
        int availableLines = ctx.AvailableLines;

        if (messages.Count == 0)
        {
            ctx.WritePaddedLine("[dim]  No messages yet[/]");
            return 1;
        }

        // Determine scroll window centered on selected index.
        // Reserve lines for the "↑ more" / "↓ more" indicators so we
        // never exceed availableLines total.
        var startIdx = 0;
        var endIdx = messages.Count;
        if (messages.Count > availableLines)
        {
            var half = availableLines / 2;
            startIdx = Math.Max(0, _store.SelectedIndex - half);
            endIdx = Math.Min(messages.Count, startIdx + availableLines);
            if (endIdx == messages.Count)
                startIdx = Math.Max(0, endIdx - availableLines);

            // Shrink the window to leave room for indicators
            bool hasUpIndicator = startIdx > 0;
            bool hasDownIndicator = endIdx < messages.Count;
            int indicatorLines = (hasUpIndicator ? 1 : 0) + (hasDownIndicator ? 1 : 0);
            if (indicatorLines > 0)
            {
                int dataLines = availableLines - indicatorLines;
                half = dataLines / 2;
                startIdx = Math.Max(0, _store.SelectedIndex - half);
                endIdx = Math.Min(messages.Count, startIdx + dataLines);
                if (endIdx == messages.Count)
                    startIdx = Math.Max(0, endIdx - dataLines);
            }
        }

        if (startIdx > 0)
        {
            ctx.WritePaddedLine($"  [dim]↑ {startIdx} more[/]");
            linesRendered++;
        }

        for (int i = startIdx; i < endIdx; i++)
        {
            var msg = messages[i];
            var isSelected = i == _store.SelectedIndex;
            var selector = isSelected ? "[bold cyan]>[/]" : " ";
            var highlight = isSelected ? "bold" : "dim";
            var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            if (msg.IsStderr)
            {
                var lineCount = msg.StderrLines.Count;
                var preview = msg.StderrLines.FirstOrDefault() ?? "";
                var width = LabelWidthFor(ctx.TermWidth);
                var padded = Truncate(preview, width).PadRight(width);
                ctx.WritePaddedLine(
                    $"  {selector} [{highlight}]{time}  !![/]  [red]{Markup.Escape(padded)}[/] [red]{lineCount} line{(lineCount == 1 ? "" : "s")}[/]");
            }
            else
            {
                var arrow = msg.IsSent ? "->" : "<-";
                var statusLabel = MessageStatusStyles.Label(msg.Status);
                var statusColor = MessageStatusStyles.Color(msg.Status);
                var label = BuildLabelMarkup(msg.Method, msg.Detail, highlight, LabelWidthFor(ctx.TermWidth));
                ctx.WritePaddedLine(
                    $"  {selector} [{highlight}]{time}  {arrow}[/]  {label} [{statusColor}]{statusLabel}[/]");
            }
            linesRendered++;
        }

        if (endIdx < messages.Count)
        {
            ctx.WritePaddedLine($"  [dim]↓ {messages.Count - endIdx} more[/]");
            linesRendered++;
        }

        return linesRendered;
    }

    /// <summary>
    /// Visible columns to the left of the method/detail column:
    /// <c>"  &gt; HH:mm:ss  &lt;-  "</c> (2 indent + 1 selector + 1 space + 8 time + 2 + 2 arrow + 2).
    /// </summary>
    private const int RowPrefixWidth = 18;

    /// <summary>Columns reserved to the right of the label: a space plus the widest status word.</summary>
    private const int RowStatusReserve = 9;

    /// <summary>
    /// Upper bound on the method/detail column. The backend already truncates a detail to ~100 chars,
    /// so growing past this only adds trailing blanks on very wide terminals while keeping the status
    /// column from drifting to the far edge.
    /// </summary>
    private const int MaxLabelWidth = 140;

    /// <summary>
    /// Width of the method/detail column, grown to fill the terminal so details aren't clipped on
    /// wide windows, clamped to <see cref="MaxLabelWidth"/> so the trailing status stays aligned.
    /// </summary>
    private static int LabelWidthFor(int termWidth)
        => Math.Min(MaxLabelWidth, Math.Max(10, termWidth - RowPrefixWidth - RowStatusReserve));

    /// <summary>
    /// Builds the markup for the method/detail column: the method in the row's highlight style
    /// followed by an optional muted <c>: detail</c>, truncated and space-padded to
    /// <paramref name="width"/> so the trailing status column lines up across rows.
    /// </summary>
    private static string BuildLabelMarkup(string method, string? detail, string highlight, int width)
    {
        method ??= "";
        detail ??= "";

        string methodVisible;
        string detailVisible;

        if (method.Length >= width || detail.Length == 0)
        {
            methodVisible = Truncate(method, width);
            detailVisible = "";
        }
        else
        {
            methodVisible = method;
            // 2 columns are taken by the ": " separator.
            var remaining = width - method.Length - 2;
            detailVisible = remaining <= 1 ? "" : Truncate(detail, remaining);
        }

        var visibleLen = methodVisible.Length + (detailVisible.Length > 0 ? 2 + detailVisible.Length : 0);
        var pad = Math.Max(0, width - visibleLen);

        var sb = new StringBuilder();
        sb.Append('[').Append(highlight).Append(']').Append(Markup.Escape(methodVisible)).Append("[/]");
        if (detailVisible.Length > 0)
            sb.Append("[grey]: ").Append(Markup.Escape(detailVisible)).Append("[/]");
        if (pad > 0)
            sb.Append(new string(' ', pad));
        return sb.ToString();
    }

    private static string Truncate(string value, int width)
    {
        if (width <= 0)
            return "";
        if (value.Length <= width)
            return value;
        return width == 1 ? "…" : value[..(width - 1)] + "…";
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
