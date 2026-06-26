namespace Lspeek.Tui.Interactive.Framework;

/// <summary>
/// Describes the result of handling a key press in a view.
/// The host uses this to manage the view stack.
/// </summary>
public abstract record Navigation
{
    private Navigation() { }

    /// <summary>
    /// Stay on the current view (no-op).
    /// </summary>
    public sealed record Stay : Navigation;

    /// <summary>
    /// Push a new view onto the stack. The current view is preserved
    /// and can be returned to via <see cref="Pop"/>.
    /// </summary>
    public sealed record Push(Type ViewType, object? Args = null) : Navigation;

    /// <summary>
    /// Pop back to the previous view on the stack.
    /// If the stack is empty, this is equivalent to <see cref="Exit"/>.
    /// </summary>
    public sealed record Pop : Navigation;

    /// <summary>
    /// Replace the current view with a new one (no back navigation to replaced view).
    /// </summary>
    public sealed record Replace(Type ViewType, object? Args = null) : Navigation;

    /// <summary>
    /// Pop back multiple levels. Useful when a nested flow completes
    /// (e.g. params editor finishing a resend flow pops back past the detail view).
    /// </summary>
    public sealed record PopTo(Type ViewType) : Navigation;

    /// <summary>
    /// Exit the application.
    /// </summary>
    public sealed record ExitApp : Navigation;
}
