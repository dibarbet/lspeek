using ManualLspClient.Core.MetaModel;
using Spectre.Console;
using System.Text;

namespace ManualLspClient.Tui.Interactive;

/// <summary>
/// Builds LSP request/notification params interactively using the metamodel for template generation.
/// For known LSP methods the protocol determines whether it is a request or notification.
/// For custom methods the user toggles on the params screen.
/// </summary>
public class RequestBuilder
{
    private const string CustomMethodLabel = "Custom method...";

    private readonly LspMetaModelProvider _metaModel;
    private readonly RequestTemplateGenerator _templateGenerator;
    private readonly HashSet<string> _notificationMethods;

    /// <summary>
    /// Flat list of display rows for the method picker.
    /// Category headers are non-selectable; method items are selectable.
    /// </summary>
    private readonly List<PickerRow> _rows = [];

    /// <summary>
    /// Indices into <see cref="_rows"/> that are selectable (i.e. not category headers).
    /// </summary>
    private readonly List<int> _selectableIndices = [];

    public RequestBuilder(LspMetaModelProvider metaModel)
    {
        _metaModel = metaModel;
        _templateGenerator = new RequestTemplateGenerator(metaModel);
        _notificationMethods = metaModel.GetClientNotifications()
            .Select(n => n.Method)
            .ToHashSet(StringComparer.Ordinal);

        BuildPickerRows();
    }

    private void BuildPickerRows()
    {
        var requestMethods = _metaModel.GetClientRequests().Select(r => r.Method);
        var notificationMethods = _metaModel.GetClientNotifications().Select(n => n.Method);
        var allMethods = requestMethods.Concat(notificationMethods).Distinct().ToList();
        var grouped = RequestTemplateGenerator.GroupMethodsByCategory(allMethods);

        // Special items at top
        _rows.Add(new PickerRow(CustomMethodLabel, IsHeader: false));
        _selectableIndices.Add(0);

        foreach (var (category, categoryMethods) in grouped)
        {
            _rows.Add(new PickerRow(category, IsHeader: true));
            foreach (var method in categoryMethods)
            {
                _selectableIndices.Add(_rows.Count);
                _rows.Add(new PickerRow(method, IsHeader: false));
            }
        }
    }

    /// <summary>
    /// Interactively picks a method and builds params.
    /// Returns (method, paramsJson, isNotification) or all-null if cancelled.
    /// </summary>
    public (string? method, string? paramsJson, bool isNotification) Build()
    {
        var (selected, isCustom) = RunMethodPicker();
        if (selected is null)
            return (null, null, false);

        return RunParamsEditor(selected, existingParams: null, isCustom);
    }

    /// <summary>
    /// Skips the method picker and pre-populates method and params for resending.
    /// Returns (method, paramsJson, isNotification) or all-null if cancelled.
    /// </summary>
    public (string? method, string? paramsJson, bool isNotification) BuildWithPreset(string method, string? existingParams)
    {
        bool isCustom = !_metaModel.GetClientRequests().Any(r => r.Method == method)
                     && !_metaModel.GetClientNotifications().Any(n => n.Method == method);
        return RunParamsEditor(method, existingParams, isCustom);
    }

    // ─── Method Picker ───────────────────────────────────────────────

    /// <summary>
    /// Custom key-driven method picker. When "Custom method..." is highlighted,
    /// typed characters build the custom method name inline.
    /// Returns (method, isCustom) or (null, false) if cancelled.
    /// </summary>
    private (string? method, bool isCustom) RunMethodPicker()
    {
        int cursor = 0;
        var customInput = new StringBuilder();
        AnsiConsole.Clear(); // Clear once on entry

        while (true)
        {
            bool onCustom = _selectableIndices[cursor] == 0;
            RenderPicker(cursor, onCustom ? customInput.ToString() : null);
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    if (cursor > 0) cursor--;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    if (cursor < _selectableIndices.Count - 1) cursor++;
                    break;
                case ConsoleKey.Enter:
                    if (onCustom)
                    {
                        var name = customInput.ToString().Trim();
                        if (name.Length == 0) break; // require a name
                        return (name, true);
                    }
                    return (_rows[_selectableIndices[cursor]].Label, false);
                case ConsoleKey.Escape:
                    return (null, false);
                case ConsoleKey.Backspace:
                    if (onCustom && customInput.Length > 0)
                        customInput.Length--;
                    break;
                default:
                    if (onCustom && !char.IsControl(key.KeyChar))
                        customInput.Append(key.KeyChar);
                    break;
            }
        }
    }

    private void RenderPicker(int cursor, string? customInput)
    {
        Console.CursorVisible = false;
        Console.SetCursorPosition(0, 0);

        int termWidth = Console.WindowWidth;

        AnsiConsole.Write(new Rule("[bold]Select method[/]")
            .LeftJustified()
            .RuleStyle(new Style(Color.Blue)));

        int selectedRowIdx = _selectableIndices[cursor];
        int availableLines = Math.Max(5, Console.WindowHeight - 5);

        // Determine scroll window centered on the selected row
        int startRow = 0;
        int endRow = _rows.Count;
        if (_rows.Count > availableLines)
        {
            int half = availableLines / 2;
            startRow = Math.Max(0, selectedRowIdx - half);
            endRow = Math.Min(_rows.Count, startRow + availableLines);
            if (endRow == _rows.Count)
                startRow = Math.Max(0, endRow - availableLines);
        }

        int renderedLines = 0;

        if (startRow > 0)
        {
            WriteLinePadded("  [dim]↑ more[/]", 8, termWidth);
            renderedLines++;
        }

        for (int i = startRow; i < endRow; i++)
        {
            var row = _rows[i];
            if (row.IsHeader)
            {
                WriteLinePadded($"  [blue]{Markup.Escape(row.Label)}[/]", row.Label.Length + 2, termWidth);
            }
            else
            {
                bool isSelected = i == selectedRowIdx;
                var selector = isSelected ? "[bold cyan]>[/]" : " ";

                // Custom method row shows inline input
                if (i == 0)
                {
                    if (isSelected && customInput is not null)
                    {
                        if (customInput.Length > 0)
                            WriteLinePadded($"  {selector} [bold]Custom: {Markup.Escape(customInput)}█[/]", customInput.Length + 14, termWidth);
                        else
                            WriteLinePadded($"  {selector} [bold]Custom method[/] [dim](type method name)█[/]", 40, termWidth);
                    }
                    else
                    {
                        var highlight = isSelected ? "bold" : "dim";
                        WriteLinePadded($"  {selector} [{highlight}]Custom method...[/]", 22, termWidth);
                    }
                }
                else
                {
                    var highlight = isSelected ? "bold" : "dim";
                    WriteLinePadded($"  {selector} [{highlight}]{Markup.Escape(row.Label)}[/]", row.Label.Length + 5, termWidth);
                }
            }
            renderedLines++;
        }

        if (endRow < _rows.Count)
        {
            WriteLinePadded("  [dim]↓ more[/]", 8, termWidth);
            renderedLines++;
        }

        // Clear any leftover lines from previous render
        int totalSlots = availableLines + 2; // +2 for possible ↑/↓ indicators
        for (int i = renderedLines; i < totalSlots; i++)
        {
            Console.Write(new string(' ', termWidth));
        }

        AnsiConsole.Write(new Rule("[dim][[Enter]] Select  [[↑/↓]] Move  [[Esc]] Cancel[/]")
            .RuleStyle(new Style(Color.Grey))
            .LeftJustified());
    }

    /// <summary>
    /// Writes a markup line padded to terminal width to avoid stale content.
    /// </summary>
    private static void WriteLinePadded(string markup, int estimatedVisibleLen, int termWidth)
    {
        int pad = Math.Max(0, termWidth - estimatedVisibleLen);
        AnsiConsole.MarkupLine($"{markup}{new string(' ', pad)}");
    }

    // ─── Params Editor (inline multi-line text editor) ─────────────

    /// <summary>
    /// Inline JSON editor. The JSON is directly editable on screen.
    /// Ctrl+Enter sends, Escape cancels, Ctrl+T toggles type (custom only).
    /// </summary>
    private (string? method, string? paramsJson, bool isNotification) RunParamsEditor(
        string method, string? existingParams, bool isCustom)
    {
        var defaultJson = existingParams ?? _templateGenerator.GenerateTemplate(method);
        var lines = new List<string>(
            defaultJson?.ReplaceLineEndings("\n").Split('\n') ?? [""]);
        if (lines.Count == 0) lines.Add("");
        bool isNotification = isCustom ? false : _notificationMethods.Contains(method);
        int cursorLine = 0;
        int cursorCol = 0;
        int scrollOffset = 0;
        AnsiConsole.Clear(); // Clear once on entry

        while (true)
        {
            // Compute available editor height (header=2, footer=1)
            int editorHeight = Math.Max(3, Console.WindowHeight - 3);

            // Keep cursor in bounds
            cursorLine = Math.Clamp(cursorLine, 0, lines.Count - 1);
            cursorCol = Math.Clamp(cursorCol, 0, lines[cursorLine].Length);

            // Auto-scroll to keep cursor visible
            if (cursorLine < scrollOffset)
                scrollOffset = cursorLine;
            if (cursorLine >= scrollOffset + editorHeight)
                scrollOffset = cursorLine - editorHeight + 1;

            RenderEditor(method, lines, isNotification, isCustom, cursorLine, cursorCol, scrollOffset, editorHeight);
            var key = Console.ReadKey(true);

            // Ctrl+Enter → send
            if (key.Key == ConsoleKey.Enter && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var json = string.Join('\n', lines).Trim();
                return (method, json.Length > 0 ? json : null, isNotification);
            }

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return (null, null, false);

                case ConsoleKey.Enter:
                    // Split line at cursor
                    var before = lines[cursorLine][..cursorCol];
                    var after = lines[cursorLine][cursorCol..];
                    lines[cursorLine] = before;
                    lines.Insert(cursorLine + 1, after);
                    cursorLine++;
                    cursorCol = 0;
                    break;

                case ConsoleKey.Backspace:
                    if (cursorCol > 0)
                    {
                        lines[cursorLine] = lines[cursorLine].Remove(cursorCol - 1, 1);
                        cursorCol--;
                    }
                    else if (cursorLine > 0)
                    {
                        // Join with previous line
                        cursorCol = lines[cursorLine - 1].Length;
                        lines[cursorLine - 1] += lines[cursorLine];
                        lines.RemoveAt(cursorLine);
                        cursorLine--;
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorCol < lines[cursorLine].Length)
                    {
                        lines[cursorLine] = lines[cursorLine].Remove(cursorCol, 1);
                    }
                    else if (cursorLine < lines.Count - 1)
                    {
                        lines[cursorLine] += lines[cursorLine + 1];
                        lines.RemoveAt(cursorLine + 1);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorCol > 0) cursorCol--;
                    else if (cursorLine > 0) { cursorLine--; cursorCol = lines[cursorLine].Length; }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorCol < lines[cursorLine].Length) cursorCol++;
                    else if (cursorLine < lines.Count - 1) { cursorLine++; cursorCol = 0; }
                    break;

                case ConsoleKey.UpArrow:
                    if (cursorLine > 0) { cursorLine--; cursorCol = Math.Min(cursorCol, lines[cursorLine].Length); }
                    break;

                case ConsoleKey.DownArrow:
                    if (cursorLine < lines.Count - 1) { cursorLine++; cursorCol = Math.Min(cursorCol, lines[cursorLine].Length); }
                    break;

                case ConsoleKey.Home:
                    cursorCol = 0;
                    break;

                case ConsoleKey.End:
                    cursorCol = lines[cursorLine].Length;
                    break;

                case ConsoleKey.Tab:
                    // Insert two spaces
                    lines[cursorLine] = lines[cursorLine].Insert(cursorCol, "  ");
                    cursorCol += 2;
                    break;

                case ConsoleKey.T:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && isCustom)
                    {
                        isNotification = !isNotification;
                        break;
                    }
                    goto default;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        lines[cursorLine] = lines[cursorLine].Insert(cursorCol, key.KeyChar.ToString());
                        cursorCol++;
                    }
                    break;
            }
        }
    }

    private static void RenderEditor(
        string method, List<string> lines,
        bool isNotification, bool isCustom,
        int cursorLine, int cursorCol,
        int scrollOffset, int editorHeight)
    {
        Console.CursorVisible = false;
        // Reposition instead of clearing to avoid flicker
        Console.SetCursorPosition(0, 0);

        int termWidth = Console.WindowWidth;

        // Header
        var typeLabel = isNotification ? "[yellow]Notification[/]" : "[cyan]Request[/]";
        var header = new Rule($"[bold]{Markup.Escape(method)}[/]  {typeLabel}")
            .LeftJustified()
            .RuleStyle(new Style(Color.Blue));
        AnsiConsole.Write(header);

        // Line number gutter width
        int maxLineNum = lines.Count;
        int gutterWidth = maxLineNum.ToString().Length;
        int gutterTotal = 1 + gutterWidth + 1; // space + number + space

        // Render visible lines
        int endLine = Math.Min(scrollOffset + editorHeight, lines.Count);
        for (int i = scrollOffset; i < endLine; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(gutterWidth);
            var lineContent = lines[i];
            // Truncate to fit terminal
            int maxContentWidth = Math.Max(1, termWidth - gutterTotal);

            if (i == cursorLine)
            {
                var col = Math.Min(cursorCol, lineContent.Length);
                var beforeCursor = lineContent[..col];
                var atCursor = col < lineContent.Length ? lineContent[col].ToString() : " ";
                var afterCursor = col < lineContent.Length ? lineContent[(col + 1)..] : "";

                // Compute padding to fill rest of line
                int contentLen = beforeCursor.Length + 1 + afterCursor.Length;
                int pad = Math.Max(0, maxContentWidth - contentLen);

                AnsiConsole.Markup($" [dim]{lineNum}[/] ");
                AnsiConsole.Markup($"{Markup.Escape(beforeCursor)}");
                AnsiConsole.Markup($"[black on white]{Markup.Escape(atCursor)}[/]");
                AnsiConsole.MarkupLine($"{Markup.Escape(afterCursor)}{new string(' ', pad)}");
            }
            else
            {
                int pad = Math.Max(0, maxContentWidth - lineContent.Length);
                AnsiConsole.MarkupLine($" [dim]{lineNum}[/] [grey]{Markup.Escape(lineContent)}[/]{new string(' ', pad)}");
            }
        }

        // Fill remaining editor space
        for (int i = endLine - scrollOffset; i < editorHeight; i++)
        {
            var tildes = "~".PadLeft(gutterWidth);
            int pad = Math.Max(0, termWidth - gutterTotal);
            AnsiConsole.MarkupLine($" [dim]{tildes}[/]{new string(' ', pad)}");
        }

        // Footer
        var parts = new List<string> { "[[Ctrl+Enter]] Send", "[[Esc]] Cancel" };
        if (isCustom)
            parts.Add("[[Ctrl+T]] Toggle Request/Notification");

        AnsiConsole.Write(new Rule($"[dim]{string.Join("  ", parts)}[/]")
            .RuleStyle(new Style(Color.Grey))
            .LeftJustified());
    }

    private record PickerRow(string Label, bool IsHeader);
}
