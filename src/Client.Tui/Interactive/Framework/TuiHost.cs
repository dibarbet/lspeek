using Lspeek.Tui.Session;
using Spectre.Console;

namespace Lspeek.Tui.Interactive.Framework;

/// <summary>
/// Main TUI application host. Manages the view stack, renders standard chrome
/// (header/footer), and runs the main input loop.
/// </summary>
public class TuiHost
{
    private readonly TuiStore _store;
    private readonly Dictionary<Type, Func<ITuiView>> _viewFactories = new();
    private readonly Stack<ITuiView> _viewStack = new();

    public TuiHost(TuiStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Registers a factory for a view type. The factory is called each time
    /// a new instance of the view is needed (push/replace).
    /// </summary>
    public TuiHost RegisterView<TView>(Func<TView> factory) where TView : ITuiView
    {
        _viewFactories[typeof(TView)] = () => factory();
        return this;
    }

    /// <summary>
    /// Starts the TUI with the specified initial view type.
    /// </summary>
    public async Task RunAsync<TInitialView>(object? args = null) where TInitialView : ITuiView
    {
        EnterAltScreen();
        try
        {
            AnsiConsole.Clear();
            var initialView = CreateView(typeof(TInitialView));
            initialView.OnEnter(args);
            _viewStack.Push(initialView);

            // Fire-and-forget auto-initialize so it happens inside the TUI
            if (_store.AutoInit && !_store.IsInitialized)
            {
                _ = _store.InitializeAsync();
            }

            while (!_store.CancellationToken.IsCancellationRequested && _viewStack.Count > 0)
            {
                Render();
                await HandleInputAsync();
            }
        }
        finally
        {
            ExitAltScreen();
        }
    }

    /// <summary>
    /// Switches to the terminal's alternate screen buffer. The alt buffer has no
    /// scrollback, so the full-screen redraws never leak into the user's terminal
    /// history. Crucially, resizing the window reflows only the alt buffer instead
    /// of pushing stale partial frames into the main scrollback (the artifacts you
    /// could otherwise scroll up to see). On exit the original terminal contents
    /// are restored. Terminals that don't support the mode ignore the sequence.
    /// </summary>
    private static void EnterAltScreen()
    {
        Console.Write("\x1b[?1049h");
    }

    /// <summary>
    /// Leaves the alternate screen buffer and restores cursor visibility. Safe to
    /// call more than once; a second invocation is a no-op on the terminal.
    /// </summary>
    private static void ExitAltScreen()
    {
        Console.Write("\x1b[?1049l");
        Console.CursorVisible = true;
    }

    private void Render()
    {
        if (_viewStack.Count == 0) return;

        Console.CursorVisible = false;

        // Begin a synchronized terminal update (DEC mode 2026) so the whole frame
        // is presented atomically. Without this, the full-screen redraw clears the
        // progress overlay region (via the views' \x1b[K) before repainting it on
        // top, which the terminal shows as a flicker. Terminals that don't support
        // the mode simply ignore these sequences.
        Console.Write("\x1b[?2026h");
        Console.SetCursorPosition(0, 0);

        var ctx = new RenderContext(Console.WindowWidth, Console.WindowHeight);

        RenderHeader(ctx);
        _viewStack.Peek().Render(ctx);
        RenderFooter(ctx);
        RenderProgressOverlay(ctx);

        // Clear anything below the footer
        Console.Write("\x1b[J");

        // End the synchronized update; the terminal now presents the frame.
        Console.Write("\x1b[?2026l");
    }

    private void RenderHeader(RenderContext ctx)
    {
        var statusIcon = _store.IsServerRunning ? "[green]●[/]" : "[red]●[/]";
        var statusText = _store.IsServerRunning ? "Running" : "Stopped";
        var pidText = $"PID {_store.ServerProcessId}";
        var initText = _store.IsInitialized ? "[green]Init[/]" : "[yellow]No Init[/]";
        var msgCount = _store.MessageCount;

        var headerText = $" lspeek - Session: [bold]{Markup.Escape(_store.ServerName)}[/]";
        var rightText = $"Server: {statusIcon} {statusText}  {pidText}  {initText}  Messages: {msgCount} ";
        ctx.WritePaddedLine($"{headerText}[dim] │ [/]{rightText}");
        ctx.WritePaddedLine($"[blue]{new string('─', ctx.TermWidth - 1)}[/]");
    }

    private void RenderFooter(RenderContext ctx)
    {
        if (_viewStack.Count == 0) return;

        var hints = _viewStack.Peek().GetFooterHints();
        var parts = hints.Select(h => $"[[{h.Key}]] {h.Label}");

        ctx.WritePaddedLine($"[grey]{new string('─', ctx.TermWidth - 1)}[/]");
        ctx.WritePaddedLine($"[dim]{string.Join("  ", parts)}[/]");
    }

    private async Task HandleInputAsync()
    {
        if (_viewStack.Count == 0) return;

        // Poll for input frequently so keypress latency stays low, while only
        // re-rendering on the slower cadence needed for background updates.
        var renderInterval = _store.HasActiveProgress ? 100 : 250;
        var nextRenderAt = Environment.TickCount64 + renderInterval;
        while (!Console.KeyAvailable)
        {
            if (_store.CancellationToken.IsCancellationRequested) return;

            await Task.Delay(25);

            if (Environment.TickCount64 >= nextRenderAt)
            {
                Render();
                renderInterval = _store.HasActiveProgress ? 100 : 250;
                nextRenderAt = Environment.TickCount64 + renderInterval;
            }
        }

        // Drain all buffered keys before the next render so that holding
        // a key down doesn't queue a render-per-keypress backlog.
        // Stop early if a key triggers actual navigation (Push/Pop/etc.).
        do
        {
            var key = Console.ReadKey(true);
            var nav = await _viewStack.Peek().HandleKeyAsync(key);
            if (nav is not Navigation.Stay)
            {
                await ApplyNavigationAsync(nav);
                return;
            }
        } while (Console.KeyAvailable);
    }

    // ── Progress overlay ──

    private const int ProgressPanelWidth = 36;
    private const int ProgressPanelInnerWidth = ProgressPanelWidth - 4; // "│ " left, " │" right

    private void RenderProgressOverlay(RenderContext ctx)
    {
        var items = _store.GetProgressItems();
        if (items.Count == 0) return;
        if (ctx.TermWidth < ProgressPanelWidth + 20) return;

        int startCol = ctx.TermWidth - ProgressPanelWidth + 1; // 1-based ANSI column
        int startRow = 3; // 1-based, below 2-line header

        // Limit items so the panel doesn't overflow the content area
        int maxLines = Math.Max(1, ctx.AvailableLines - 2);
        var visibleItems = items.Take(maxLines).ToList();

        Console.Write("\x1b[s"); // save cursor

        // Top border
        // Top border. Fixed literal is "┌─ Progress " (12) + "┐" (1) = 13 chars,
        // so the dash fill is the remaining width to keep the panel exactly
        // ProgressPanelWidth wide and aligned with the rows below.
        var dashes = ProgressPanelWidth - 13;
        Console.Write($"\x1b[{startRow};{startCol}H\x1b[36m┌─ Progress {new string('─', dashes)}┐\x1b[0m");

        int row = startRow + 1;
        foreach (var item in visibleItems)
        {
            if (item.State == ProgressItemState.Ended)
            {
                var title = TruncateRaw(item.Title, ProgressPanelInnerWidth - 2);
                var pad = ProgressPanelInnerWidth - 2 - title.Length;
                Console.Write($"\x1b[{row};{startCol}H\x1b[2m│ \x1b[32m✓\x1b[0m\x1b[2m {title}{new string(' ', pad)} │\x1b[0m");
                row++;
            }
            else
            {
                // Title line with optional percentage
                string pctSuffix = item.Percentage.HasValue ? $" {item.Percentage,3}%" : "";
                int titleMax = ProgressPanelInnerWidth - 2 - pctSuffix.Length;
                var title = TruncateRaw(item.Title, titleMax);
                var pad = titleMax - title.Length;
                Console.Write($"\x1b[{row};{startCol}H│ \x1b[33m●\x1b[0m {title}{new string(' ', pad)}{pctSuffix} │");
                row++;

                // Message line (if message exists and we have room)
                if (item.Message is not null && row - startRow < maxLines)
                {
                    var msg = TruncateRaw(item.Message, ProgressPanelInnerWidth - 2);
                    var msgPad = ProgressPanelInnerWidth - 2 - msg.Length;
                    Console.Write($"\x1b[{row};{startCol}H│   \x1b[2m{msg}{new string(' ', Math.Max(0, msgPad))}\x1b[0m │");
                    row++;
                }
            }
        }

        // Bottom border
        Console.Write($"\x1b[{row};{startCol}H\x1b[36m└{new string('─', ProgressPanelWidth - 2)}┘\x1b[0m");

        Console.Write("\x1b[u"); // restore cursor
    }

    private static string TruncateRaw(string text, int maxLen)
    {
        if (maxLen <= 0) return "";
        if (text.Length <= maxLen) return text;
        return text[..(maxLen - 1)] + "…";
    }

    // ── Navigation ──

    private async Task ApplyNavigationAsync(Navigation nav)
    {
        switch (nav)
        {
            case Navigation.Stay:
                break;

            case Navigation.Push push:
                var pushView = CreateView(push.ViewType);
                pushView.OnEnter(push.Args);
                _viewStack.Push(pushView);
                break;

            case Navigation.Replace replace:
                _viewStack.Pop();
                var replaceView = CreateView(replace.ViewType);
                replaceView.OnEnter(replace.Args);
                _viewStack.Push(replaceView);
                break;

            case Navigation.Pop:
                _viewStack.Pop();
                if (_viewStack.Count == 0)
                {
                    await _store.ShutdownAsync();
                }
                break;

            case Navigation.PopTo popTo:
                while (_viewStack.Count > 1 && _viewStack.Peek().GetType() != popTo.ViewType)
                {
                    _viewStack.Pop();
                }
                break;

            case Navigation.ExitApp:
                // Leave the alt screen first so the shutdown messages appear in
                // the user's normal terminal and persist after the TUI exits.
                ExitAltScreen();
                AnsiConsole.MarkupLine("[dim]Shutting down...[/]");
                await _store.ShutdownAsync();
                AnsiConsole.MarkupLine("[green]Session ended.[/]");
                _viewStack.Clear();
                break;
        }
    }

    private ITuiView CreateView(Type viewType)
    {
        if (_viewFactories.TryGetValue(viewType, out var factory))
            return factory();

        throw new InvalidOperationException(
            $"No factory registered for view type '{viewType.Name}'. " +
            $"Call RegisterView<{viewType.Name}>() before running the host.");
    }
}
