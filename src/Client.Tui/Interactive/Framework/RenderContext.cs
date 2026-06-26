using Spectre.Console;

namespace Lspeek.Tui.Interactive.Framework;

/// <summary>
/// Provides terminal-aware rendering helpers for views.
/// Created fresh each render cycle with current terminal dimensions.
/// </summary>
public class RenderContext
{
    public int TermWidth { get; }

    /// <summary>
    /// Number of content lines available between header and footer.
    /// </summary>
    public int AvailableLines { get; }

    /// <summary>Header lines consumed by the host (status bar + separator).</summary>
    private const int HeaderLines = 2;

    /// <summary>Footer lines consumed by the host (separator + hints).</summary>
    private const int FooterLines = 2;

    public RenderContext(int termWidth, int termHeight)
    {
        TermWidth = termWidth;
        AvailableLines = Math.Max(5, termHeight - HeaderLines - FooterLines - 2);
    }

    /// <summary>
    /// Writes a Spectre markup line and clears to end of line to erase stale content.
    /// </summary>
    public void WritePaddedLine(string markup)
    {
        AnsiConsole.Markup(markup);
        Console.Write("\x1b[K\n");
    }

    /// <summary>
    /// Writes a markup line padded with spaces to a specific estimated visible length.
    /// Used when ANSI clear isn't sufficient (e.g. inside Spectre widgets).
    /// </summary>
    public void WriteLinePadded(string markup, int estimatedVisibleLen)
    {
        int pad = Math.Max(0, TermWidth - estimatedVisibleLen);
        AnsiConsole.MarkupLine($"{markup}{new string(' ', pad)}");
    }

    /// <summary>
    /// Wraps a line to the available terminal width using hard wraps so long
    /// JSON strings remain visible without resizing the window.
    /// </summary>
    public static IReadOnlyList<string> WrapLine(string line, int maxWidth)
    {
        if (maxWidth <= 0)
            return [""];

        if (string.IsNullOrEmpty(line))
            return [""];

        var wrapped = new List<string>((line.Length / maxWidth) + 1);
        for (var start = 0; start < line.Length; start += maxWidth)
        {
            var length = Math.Min(maxWidth, line.Length - start);
            wrapped.Add(line.Substring(start, length));
        }

        return wrapped;
    }

    /// <summary>
    /// Clears remaining lines in the content area to remove stale content.
    /// </summary>
    public void ClearRemainingLines(int linesRendered)
    {
        for (int i = linesRendered; i < AvailableLines; i++)
        {
            Console.Write("\x1b[K\n");
        }
    }
}
