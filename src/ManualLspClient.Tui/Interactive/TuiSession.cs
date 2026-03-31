using ManualLspClient.Core.MetaModel;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Tui.Scripting;
using Spectre.Console;
using System.Text.Json;

namespace ManualLspClient.Tui.Interactive;

/// <summary>
/// Main interactive TUI with hotkey-driven session log navigation.
/// Layout: header bar, session log (collapsed), action prompt, hotkey footer.
/// </summary>
public class TuiSession
{
    private readonly LspSession _session;
    private readonly LspMetaModelProvider _metaModel;
    private readonly RequestBuilder _requestBuilder;
    private readonly CancellationTokenSource _cts = new();
    private int _selectedIndex = -1;
    private int? _expandedIndex;
    private int _jsonScrollOffset;

    public TuiSession(LspSession session, LspMetaModelProvider metaModel)
    {
        _session = session;
        _metaModel = metaModel;
        _requestBuilder = new RequestBuilder(metaModel);
    }

    public async Task RunAsync()
    {
        AnsiConsole.Clear(); // Clear once on entry
        while (!_cts.Token.IsCancellationRequested)
        {
            Render();
            await HandleInputAsync();
        }
    }

    private void Render()
    {
        Console.CursorVisible = false;
        Console.SetCursorPosition(0, 0);
        var messages = _session.Log.GetAll();
        int termWidth = Console.WindowWidth;
        int termHeight = Console.WindowHeight;

        // Update selected index to track latest
        if (_selectedIndex < 0 || _selectedIndex >= messages.Count)
            _selectedIndex = messages.Count - 1;

        // ── Header ──
        RenderHeader();

        // ── Session log or expanded detail ──
        // Track how many content lines we render so we can clear the rest
        int contentLines;
        int availableLines = Math.Max(5, termHeight - 6); // header(2) + footer(2) + buffer(2)

        if (_expandedIndex.HasValue && _expandedIndex.Value < messages.Count)
        {
            contentLines = RenderExpandedMessage(messages[_expandedIndex.Value], availableLines, termWidth);
        }
        else
        {
            contentLines = RenderCollapsedLog(messages, availableLines, termWidth);
        }

        // Clear any leftover lines from previous render
        for (int i = contentLines; i < availableLines; i++)
        {
            Console.Write("\x1b[K\n"); // Clear line and advance
        }

        // ── Footer ──
        RenderFooter();

        // Clear anything below the footer (handles view transitions)
        Console.Write("\x1b[J"); // ANSI: clear from cursor to end of screen
    }

    private void RenderHeader()
    {
        var statusIcon = _session.IsServerRunning ? "[green]●[/]" : "[red]●[/]";
        var statusText = _session.IsServerRunning ? "Running" : "Stopped";
        var pidText = $"PID {_session.ServerProcessId}";
        var initText = _session.IsInitialized ? "[green]Init[/]" : "[yellow]No Init[/]";
        var msgCount = _session.Log.Count;
        int termWidth = Console.WindowWidth;

        var headerText = $" ManualLspClient - Session: [bold]{Markup.Escape(_session.ServerConfig.Name)}[/]";
        var rightText = $"Server: {statusIcon} {statusText}  {pidText}  {initText}  Messages: {msgCount} ";
        WritePaddedLine($"{headerText}[dim] │ [/]{rightText}", termWidth);
        WritePaddedLine($"[blue]{new string('─', termWidth - 1)}[/]", termWidth);
    }

    private int RenderCollapsedLog(IReadOnlyList<SessionMessage> messages, int availableLines, int termWidth)
    {
        int linesRendered = 0;

        if (messages.Count == 0)
        {
            WritePaddedLine("[dim]  No messages yet[/]", termWidth);
            return 1;
        }

        // Determine scroll window
        var startIdx = 0;
        var endIdx = messages.Count;
        if (messages.Count > availableLines)
        {
            var half = availableLines / 2;
            startIdx = Math.Max(0, _selectedIndex - half);
            endIdx = Math.Min(messages.Count, startIdx + availableLines);
            if (endIdx == messages.Count)
                startIdx = Math.Max(0, endIdx - availableLines);
        }

        if (startIdx > 0)
        {
            WritePaddedLine($"  [dim]↑ {startIdx} more[/]", termWidth);
            linesRendered++;
        }

        for (int i = startIdx; i < endIdx; i++)
        {
            var msg = messages[i];
            var isSelected = i == _selectedIndex;
            var selector = isSelected ? "[bold cyan]>[/]" : " ";
            var highlight = isSelected ? "bold" : "dim";
            var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            if (msg.IsStderr)
            {
                var lineCount = msg.StderrLines.Count;
                var preview = msg.StderrLines.FirstOrDefault() ?? "";
                if (preview.Length > 60) preview = preview[..60] + "…";
                WritePaddedLine(
                    $"  {selector} [{highlight}]{time}  !!  [red]{Markup.Escape(preview),-40}[/][/] [red]{lineCount} line{(lineCount == 1 ? "" : "s")}[/]", termWidth);
            }
            else
            {
                var arrow = msg.Direction == MessageDirection.Sent ? "->" : "<-";
                var statusLabel = msg.GetStatusLabel();
                var statusColor = msg.GetStatusColor();
                WritePaddedLine(
                    $"  {selector} [{highlight}]{time}  {arrow}  {Markup.Escape(msg.Method),-40}[/] [{statusColor}]{statusLabel}[/]", termWidth);
            }
            linesRendered++;
        }

        if (endIdx < messages.Count)
        {
            WritePaddedLine($"  [dim]↓ {messages.Count - endIdx} more[/]", termWidth);
            linesRendered++;
        }

        return linesRendered;
    }

    private int RenderExpandedMessage(SessionMessage msg, int totalAvailable, int termWidth)
    {
        if (msg.IsStderr)
        {
            return RenderExpandedStderr(msg, totalAvailable, termWidth);
        }

        int linesRendered = 0;

        var arrow = msg.Direction == MessageDirection.Sent ? "Sent" : "Received";
        var typeLabel = msg.MessageType.ToString();
        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var statusColor = msg.GetStatusColor();

        WritePaddedLine($"  [bold]Message Details[/]  [dim](index {_expandedIndex})[/]", termWidth);
        linesRendered++;
        WritePaddedLine($"[grey]{new string('─', termWidth - 1)}[/]", termWidth);
        linesRendered++;

        // Render metadata as simple lines instead of Table (avoids variable-height rendering)
        WritePaddedLine($"  [bold]Time:[/] {time}  [bold]Direction:[/] {arrow}  [bold]Type:[/] {typeLabel}  [bold]Status:[/] [{statusColor}]{msg.GetStatusLabel()}[/]", termWidth);
        linesRendered++;
        var idLabel = msg.Id.HasValue ? $"  [bold]Id:[/] {msg.Id.Value}" : "";
        WritePaddedLine($"  [bold]Method:[/] {Markup.Escape(msg.Method)}{idLabel}", termWidth);
        linesRendered++;

        // JSON body with scrolling
        if (msg.Json.HasValue)
        {
            var label = msg.MessageType == MessageType.Response ? "Result" : "Params";
            var formatted = msg.GetFormattedJson();
            var allLines = formatted.ReplaceLineEndings("\n").Split('\n');
            var jsonAvailable = Math.Max(3, totalAvailable - linesRendered - 1); // -1 for label line

            // Clamp scroll offset
            var maxOffset = Math.Max(0, allLines.Length - jsonAvailable);
            _jsonScrollOffset = Math.Clamp(_jsonScrollOffset, 0, maxOffset);

            var scrollInfo = allLines.Length > jsonAvailable
                ? $" [dim]({_jsonScrollOffset + 1}–{Math.Min(_jsonScrollOffset + jsonAvailable, allLines.Length)} of {allLines.Length})[/]"
                : "";

            WritePaddedLine($"  [bold]{label}[/]{scrollInfo}", termWidth);
            linesRendered++;

            var visibleLines = allLines.Skip(_jsonScrollOffset).Take(jsonAvailable);
            foreach (var line in visibleLines)
            {
                var display = TruncateLine(line, termWidth - 2);
                WritePaddedLine($"  [grey]{Markup.Escape(display)}[/]", termWidth);
                linesRendered++;
            }
        }

        return linesRendered;
    }

    private int RenderExpandedStderr(SessionMessage msg, int totalAvailable, int termWidth)
    {
        int linesRendered = 0;

        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

        WritePaddedLine($"  [bold red]Server stderr[/]  [dim](index {_expandedIndex})[/]", termWidth);
        linesRendered++;
        WritePaddedLine($"[red]{new string('─', termWidth - 1)}[/]", termWidth);
        linesRendered++;
        WritePaddedLine($"  [bold]Time:[/] {time}  [bold]Lines:[/] {msg.StderrLines.Count}", termWidth);
        linesRendered++;

        var allLines = msg.StderrLines;
        var stderrAvailable = Math.Max(3, totalAvailable - linesRendered - 1);
        var maxOffset = Math.Max(0, allLines.Count - stderrAvailable);
        _jsonScrollOffset = Math.Clamp(_jsonScrollOffset, 0, maxOffset);

        var scrollInfo = allLines.Count > stderrAvailable
            ? $" [dim]({_jsonScrollOffset + 1}–{Math.Min(_jsonScrollOffset + stderrAvailable, allLines.Count)} of {allLines.Count})[/]"
            : "";

        WritePaddedLine($"  [bold red]Output[/]{scrollInfo}", termWidth);
        linesRendered++;

        var visibleLines = allLines.Skip(_jsonScrollOffset).Take(stderrAvailable);
        foreach (var line in visibleLines)
        {
            var display = TruncateLine(line, termWidth - 2);
            WritePaddedLine($"  [red]{Markup.Escape(display)}[/]", termWidth);
            linesRendered++;
        }

        return linesRendered;
    }

    /// <summary>
    /// Finds the index of the matching request (for a response) or response (for a request)
    /// in the message list, matched by Id.
    /// </summary>
    private static int? FindMatchingMessageIndex(SessionMessage msg, IReadOnlyList<SessionMessage> messages)
    {
        if (!msg.Id.HasValue) return null;

        var targetType = msg.MessageType == MessageType.Request
            ? MessageType.Response
            : msg.MessageType == MessageType.Response
                ? MessageType.Request
                : (MessageType?)null;

        if (targetType is null) return null;

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].MessageType == targetType && messages[i].Id == msg.Id)
                return i;
        }

        return null;
    }

    private void RenderFooter()
    {
        int termWidth = Console.WindowWidth;

        if (_expandedIndex.HasValue)
        {
            var messages = _session.Log.GetAll();
            var msg = messages[_expandedIndex.Value];
            int? matchIndex = FindMatchingMessageIndex(msg, messages);

            var parts = new List<string> { "[[Esc]] Back", "[[↑/↓]] Scroll", "[[N/P]] Next/Prev", "[[C]] Copy JSON" };

            if (matchIndex.HasValue)
            {
                var matchLabel = msg.MessageType == MessageType.Request ? "Go to Response" : "Go to Request";
                parts.Add($"[[M]] {matchLabel}");
            }

            // Only show Resend for sent requests (re-send with same method+params)
            if (msg.MessageType == MessageType.Request && msg.Direction == MessageDirection.Sent)
            {
                parts.Add("[[R]] Resend");
            }

            parts.Add("[[Q]] Quit");

            WritePaddedLine($"[grey]{new string('─', termWidth - 1)}[/]", termWidth);
            WritePaddedLine($"[dim]{string.Join("  ", parts)}[/]", termWidth);
        }
        else
        {
            WritePaddedLine($"[grey]{new string('─', termWidth - 1)}[/]", termWidth);
            WritePaddedLine("[dim][[Enter]] Expand  [[↑/↓]] Move  [[R]] Send Request  [[X]] Export  [[Q]] Quit[/]", termWidth);
        }
    }

    private async Task HandleInputAsync()
    {
        // Poll for input so the screen refreshes periodically (e.g. for status updates)
        while (!Console.KeyAvailable)
        {
            await Task.Delay(500);
            Render();
        }

        var key = Console.ReadKey(true);
        var messages = _session.Log.GetAll();

        if (_expandedIndex.HasValue)
        {
            // Expanded view hotkeys
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                case ConsoleKey.Enter:
                    _expandedIndex = null;
                    _jsonScrollOffset = 0;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    _jsonScrollOffset++;
                    break;
                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    if (_jsonScrollOffset > 0) _jsonScrollOffset--;
                    break;
                case ConsoleKey.N:
                    if (_expandedIndex < messages.Count - 1)
                    {
                        _expandedIndex++;
                        _selectedIndex = _expandedIndex.Value;
                        _jsonScrollOffset = 0;
                    }
                    break;
                case ConsoleKey.P:
                    if (_expandedIndex > 0)
                    {
                        _expandedIndex--;
                        _selectedIndex = _expandedIndex.Value;
                        _jsonScrollOffset = 0;
                    }
                    break;
                case ConsoleKey.C:
                    CopyJsonToClipboard(messages[_expandedIndex.Value]);
                    break;
                case ConsoleKey.M:
                    JumpToMatch(messages);
                    break;
                case ConsoleKey.R:
                {
                    var msg = messages[_expandedIndex.Value];
                    if (msg.MessageType == MessageType.Request && msg.Direction == MessageDirection.Sent)
                    {
                        var method = msg.Method;
                        var paramsJson = msg.Json.HasValue ? msg.GetFormattedJson() : null;
                        _expandedIndex = null;
                        _jsonScrollOffset = 0;
                        await ResendMessageAsync(method, paramsJson);
                    }
                    break;
                }
                case ConsoleKey.X:
                    await ExportSessionAsync();
                    break;
                case ConsoleKey.Q:
                    await ShutdownAndExitAsync();
                    break;
            }
        }
        else
        {
            // Collapsed view hotkeys
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    if (_selectedIndex > 0) _selectedIndex--;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    if (_selectedIndex < messages.Count - 1) _selectedIndex++;
                    break;
                case ConsoleKey.Enter:
                    if (messages.Count > 0 && _selectedIndex >= 0)
                    {
                        _expandedIndex = _selectedIndex;
                        _jsonScrollOffset = 0;
                    }
                    break;
                case ConsoleKey.R:
                    await SendMessageAsync();
                    break;
                case ConsoleKey.X:
                    await ExportSessionAsync();
                    break;
                case ConsoleKey.Q:
                    await ShutdownAndExitAsync();
                    break;
            }
        }
    }

    private async Task SendMessageAsync()
    {
        if (!_session.IsServerRunning)
        {
            AnsiConsole.MarkupLine("[red]Server is not running. Cannot send messages.[/]");
            await Task.Delay(1500);
            return;
        }

        AnsiConsole.Clear();
        var (method, paramsJson, isNotification) = _requestBuilder.Build();
        if (method is null) return;

        await DispatchMessageAsync(method, paramsJson, isNotification);
    }

    private async Task ResendMessageAsync(string method, string? existingParamsJson)
    {
        if (!_session.IsServerRunning)
        {
            AnsiConsole.MarkupLine("[red]Server is not running. Cannot send messages.[/]");
            await Task.Delay(1500);
            return;
        }

        AnsiConsole.Clear();
        var (resolvedMethod, paramsJson, isNotification) = _requestBuilder.BuildWithPreset(method, existingParamsJson);
        if (resolvedMethod is null) return;

        await DispatchMessageAsync(resolvedMethod, paramsJson, isNotification);
    }

    private async Task DispatchMessageAsync(string method, string? paramsJson, bool isNotification)
    {
        object? paramsObj = null;
        if (paramsJson is not null)
        {
            try
            {
                paramsObj = JsonDocument.Parse(paramsJson).RootElement;
            }
            catch (JsonException ex)
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine($"[red]Invalid JSON:[/] {Markup.Escape(ex.Message)}");
                await Task.Delay(2000);
                return;
            }
        }

        if (isNotification)
        {
            try
            {
                await _session.SendNotificationAsync(method, paramsObj, _cts.Token);
            }
            catch (Exception)
            {
                // Error is captured in the session log
            }
        }
        else
        {
            try
            {
                await _session.SendRequestAsync(method, paramsObj, _cts.Token);
            }
            catch (Exception)
            {
                // Error is captured in the session log via MessageTraced
            }
        }

        _selectedIndex = _session.Log.Count - 1;
    }

    private async Task ExportSessionAsync()
    {
        AnsiConsole.Clear();
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Export path:[/]")
                .DefaultValue("session-export.json"));

        ScriptExporter.Export(_session.Log, path);
        AnsiConsole.MarkupLine($"[green]✓ Exported to {Markup.Escape(path)}[/]");
        await Task.Delay(1000);
    }

    private void CopyJsonToClipboard(SessionMessage msg)
    {
        string? text = null;
        if (msg.IsStderr)
        {
            text = string.Join('\n', msg.StderrLines);
        }
        else if (msg.Json.HasValue)
        {
            text = msg.GetFormattedJson();
        }

        if (text is null) return;

        TextCopy.ClipboardService.SetText(text);
    }

    private void JumpToMatch(IReadOnlyList<SessionMessage> messages)
    {
        if (!_expandedIndex.HasValue) return;
        var msg = messages[_expandedIndex.Value];
        var matchIndex = FindMatchingMessageIndex(msg, messages);
        if (matchIndex.HasValue)
        {
            _expandedIndex = matchIndex.Value;
            _selectedIndex = matchIndex.Value;
            _jsonScrollOffset = 0;
        }
    }

    private async Task ShutdownAndExitAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[dim]Shutting down...[/]");
        await _session.ShutdownAsync();
        _cts.Cancel();
        AnsiConsole.MarkupLine("[green]Session ended.[/]");
    }

    /// <summary>
    /// Writes a markup line and clears to end of line to erase stale content.
    /// Uses ANSI escape to clear reliably regardless of markup length.
    /// </summary>
    private static void WritePaddedLine(string markup, int termWidth)
    {
        AnsiConsole.Markup(markup);
        // ANSI: clear from cursor to end of line, then newline
        Console.Write("\x1b[K\n");
    }

    /// <summary>
    /// Truncates a string to maxWidth characters to prevent terminal line wrapping.
    /// </summary>
    private static string TruncateLine(string line, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (line.Length <= maxWidth) return line;
        return line[..(maxWidth - 1)] + "…";
    }
}
