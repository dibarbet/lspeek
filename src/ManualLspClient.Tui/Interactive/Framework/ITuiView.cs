namespace ManualLspClient.Tui.Interactive.Framework;

/// <summary>
/// A hint shown in the footer bar describing a hotkey and its action.
/// </summary>
public record FooterHint(string Key, string Label);

/// <summary>
/// Interface for a full-screen TUI view component.
/// The host manages header/footer chrome and the render loop;
/// views only render their content area and handle key input.
/// </summary>
public interface ITuiView
{
    /// <summary>
    /// Called when this view becomes the active view, with optional transition args.
    /// Use this to initialize or reset local state based on the navigation context.
    /// </summary>
    void OnEnter(object? args);

    /// <summary>
    /// Render the view's content area. The host has already rendered the header
    /// and will render the footer after this call.
    /// </summary>
    void Render(RenderContext ctx);

    /// <summary>
    /// Handle a key press. Returns a <see cref="Navigation"/> describing what to do next.
    /// </summary>
    Task<Navigation> HandleKeyAsync(ConsoleKeyInfo key);

    /// <summary>
    /// Provide footer hints for this view. The host renders these in a standard format.
    /// </summary>
    IReadOnlyList<FooterHint> GetFooterHints();
}
