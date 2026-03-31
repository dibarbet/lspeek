using Spectre.Console;

namespace ManualLspClient.Tui.Interactive.Framework;

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
        AnsiConsole.Clear();
        var initialView = CreateView(typeof(TInitialView));
        initialView.OnEnter(args);
        _viewStack.Push(initialView);

        while (!_store.CancellationToken.IsCancellationRequested && _viewStack.Count > 0)
        {
            Render();
            await HandleInputAsync();
        }
    }

    private void Render()
    {
        if (_viewStack.Count == 0) return;

        Console.CursorVisible = false;
        Console.SetCursorPosition(0, 0);

        var ctx = new RenderContext(Console.WindowWidth, Console.WindowHeight);

        RenderHeader(ctx);
        _viewStack.Peek().Render(ctx);
        RenderFooter(ctx);

        // Clear anything below the footer
        Console.Write("\x1b[J");
    }

    private void RenderHeader(RenderContext ctx)
    {
        var statusIcon = _store.IsServerRunning ? "[green]●[/]" : "[red]●[/]";
        var statusText = _store.IsServerRunning ? "Running" : "Stopped";
        var pidText = $"PID {_store.ServerProcessId}";
        var initText = _store.IsInitialized ? "[green]Init[/]" : "[yellow]No Init[/]";
        var msgCount = _store.MessageCount;

        var headerText = $" ManualLspClient - Session: [bold]{Markup.Escape(_store.ServerName)}[/]";
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

        // Poll for input so the screen refreshes periodically
        while (!Console.KeyAvailable)
        {
            if (_store.CancellationToken.IsCancellationRequested) return;
            await Task.Delay(500);
            Render();
        }

        var key = Console.ReadKey(true);
        var nav = await _viewStack.Peek().HandleKeyAsync(key);
        await ApplyNavigationAsync(nav);
    }

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
                AnsiConsole.Clear();
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
