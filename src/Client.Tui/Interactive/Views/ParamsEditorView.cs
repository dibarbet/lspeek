using ManualLspClient.Core.MetaModel;
using ManualLspClient.Tui.Interactive.Framework;
using Spectre.Console;

namespace ManualLspClient.Tui.Interactive.Views;

/// <summary>
/// Transition args for the params editor view.
/// </summary>
public record ParamsEditorArgs(string Method, bool IsCustom, string? ExistingParams = null);

/// <summary>
/// Inline JSON editor view for editing request/notification params.
/// Supports full text editing with cursor navigation and scrolling.
/// </summary>
public class ParamsEditorView : ITuiView
{
    private readonly TuiStore _store;
    private readonly RequestTemplateGenerator _templateGenerator;
    private readonly HashSet<string> _notificationMethods;

    private string _method = "";
    private bool _isCustom;
    private bool _isNotification;
    private List<string> _lines = [""];
    private int _cursorLine;
    private int _cursorCol;
    private int _scrollOffset;

    public ParamsEditorView(TuiStore store)
    {
        _store = store;
        _templateGenerator = new RequestTemplateGenerator(store.MetaModel);
        _notificationMethods = store.MetaModel.GetClientNotifications()
            .Select(n => n.Method)
            .ToHashSet(StringComparer.Ordinal);
    }

    public void OnEnter(object? args)
    {
        if (args is ParamsEditorArgs editorArgs)
        {
            _method = editorArgs.Method;
            _isCustom = editorArgs.IsCustom;

            var defaultJson = editorArgs.ExistingParams ?? _templateGenerator.GenerateTemplate(_method);
            _lines = new List<string>(
                defaultJson?.ReplaceLineEndings("\n").Split('\n') ?? [""]);
            if (_lines.Count == 0) _lines.Add("");

            _isNotification = _isCustom ? false : _notificationMethods.Contains(_method);
        }

        _cursorLine = 0;
        _cursorCol = 0;
        _scrollOffset = 0;
    }

    public void Render(RenderContext ctx)
    {
        int editorHeight = ctx.AvailableLines;

        // Keep cursor in bounds
        _cursorLine = Math.Clamp(_cursorLine, 0, _lines.Count - 1);
        _cursorCol = Math.Clamp(_cursorCol, 0, _lines[_cursorLine].Length);

        // Auto-scroll to keep cursor visible
        if (_cursorLine < _scrollOffset)
            _scrollOffset = _cursorLine;
        if (_cursorLine >= _scrollOffset + editorHeight)
            _scrollOffset = _cursorLine - editorHeight + 1;

        int linesRendered = RenderEditor(ctx, editorHeight);
        ctx.ClearRemainingLines(linesRendered);
    }

    public Task<Navigation> HandleKeyAsync(ConsoleKeyInfo key)
    {
        // Ctrl+Enter → send
        if (key.Key == ConsoleKey.Enter && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            return HandleSendAsync();
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return Task.FromResult<Navigation>(new Navigation.Pop());

            case ConsoleKey.Enter:
                SplitLineAtCursor();
                break;

            case ConsoleKey.Backspace:
                HandleBackspace();
                break;

            case ConsoleKey.Delete:
                HandleDelete();
                break;

            case ConsoleKey.LeftArrow:
                if (_cursorCol > 0) _cursorCol--;
                else if (_cursorLine > 0) { _cursorLine--; _cursorCol = _lines[_cursorLine].Length; }
                break;

            case ConsoleKey.RightArrow:
                if (_cursorCol < _lines[_cursorLine].Length) _cursorCol++;
                else if (_cursorLine < _lines.Count - 1) { _cursorLine++; _cursorCol = 0; }
                break;

            case ConsoleKey.UpArrow:
                if (_cursorLine > 0) { _cursorLine--; _cursorCol = Math.Min(_cursorCol, _lines[_cursorLine].Length); }
                break;

            case ConsoleKey.DownArrow:
                if (_cursorLine < _lines.Count - 1) { _cursorLine++; _cursorCol = Math.Min(_cursorCol, _lines[_cursorLine].Length); }
                break;

            case ConsoleKey.Home:
                _cursorCol = 0;
                break;

            case ConsoleKey.End:
                _cursorCol = _lines[_cursorLine].Length;
                break;

            case ConsoleKey.Tab:
                _lines[_cursorLine] = _lines[_cursorLine].Insert(_cursorCol, "  ");
                _cursorCol += 2;
                break;

            case ConsoleKey.T:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && _isCustom)
                {
                    _isNotification = !_isNotification;
                    break;
                }
                goto default;

            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _lines[_cursorLine] = _lines[_cursorLine].Insert(_cursorCol, key.KeyChar.ToString());
                    _cursorCol++;
                }
                break;
        }

        return Task.FromResult<Navigation>(new Navigation.Stay());
    }

    public IReadOnlyList<FooterHint> GetFooterHints()
    {
        var hints = new List<FooterHint>
        {
            new("Ctrl+Enter", "Send"),
            new("Esc", "Cancel"),
        };

        if (_isCustom)
            hints.Add(new("Ctrl+T", "Toggle Request/Notification"));

        return hints;
    }

    // ── Rendering ──

    private int RenderEditor(RenderContext ctx, int editorHeight)
    {
        int linesRendered = 0;

        // Type label in the content area (method is already shown in header by host,
        // but we show it here too for context since the header is generic)
        var typeLabel = _isNotification ? "[yellow]Notification[/]" : "[cyan]Request[/]";
        ctx.WritePaddedLine($"  [bold]{Markup.Escape(_method)}[/]  {typeLabel}");
        linesRendered++;

        // Line number gutter width
        int maxLineNum = _lines.Count;
        int gutterWidth = maxLineNum.ToString().Length;
        int gutterTotal = 1 + gutterWidth + 1; // space + number + space

        // Adjust editor height for the type label line
        int codeHeight = editorHeight - 1;

        // Render visible lines
        int endLine = Math.Min(_scrollOffset + codeHeight, _lines.Count);
        for (int i = _scrollOffset; i < endLine; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(gutterWidth);
            var lineContent = _lines[i];
            int maxContentWidth = Math.Max(1, ctx.TermWidth - gutterTotal);

            if (i == _cursorLine)
            {
                var col = Math.Min(_cursorCol, lineContent.Length);
                var beforeCursor = lineContent[..col];
                var atCursor = col < lineContent.Length ? lineContent[col].ToString() : " ";
                var afterCursor = col < lineContent.Length ? lineContent[(col + 1)..] : "";

                int contentLen = beforeCursor.Length + 1 + afterCursor.Length;
                int pad = Math.Max(0, maxContentWidth - contentLen);

                AnsiConsole.Markup($" [dim]{lineNum}[/] ");
                AnsiConsole.Markup($"{Markup.Escape(beforeCursor)}");
                AnsiConsole.Markup($"[black on white]{Markup.Escape(atCursor)}[/]");
                AnsiConsole.Markup($"{Markup.Escape(afterCursor)}{new string(' ', pad)}");
                Console.Write("\x1b[K\n");
            }
            else
            {
                int pad = Math.Max(0, maxContentWidth - lineContent.Length);
                AnsiConsole.Markup($" [dim]{lineNum}[/] [grey]{Markup.Escape(lineContent)}[/]{new string(' ', pad)}");
                Console.Write("\x1b[K\n");
            }
            linesRendered++;
        }

        // Fill remaining editor space with tildes
        for (int i = endLine - _scrollOffset; i < codeHeight; i++)
        {
            var tildes = "~".PadLeft(gutterWidth);
            int pad = Math.Max(0, ctx.TermWidth - gutterTotal);
            AnsiConsole.Markup($" [dim]{tildes}[/]{new string(' ', pad)}");
            Console.Write("\x1b[K\n");
            linesRendered++;
        }

        return linesRendered;
    }

    // ── Editing helpers ──

    private void SplitLineAtCursor()
    {
        var before = _lines[_cursorLine][.._cursorCol];
        var after = _lines[_cursorLine][_cursorCol..];
        _lines[_cursorLine] = before;
        _lines.Insert(_cursorLine + 1, after);
        _cursorLine++;
        _cursorCol = 0;
    }

    private void HandleBackspace()
    {
        if (_cursorCol > 0)
        {
            _lines[_cursorLine] = _lines[_cursorLine].Remove(_cursorCol - 1, 1);
            _cursorCol--;
        }
        else if (_cursorLine > 0)
        {
            _cursorCol = _lines[_cursorLine - 1].Length;
            _lines[_cursorLine - 1] += _lines[_cursorLine];
            _lines.RemoveAt(_cursorLine);
            _cursorLine--;
        }
    }

    private void HandleDelete()
    {
        if (_cursorCol < _lines[_cursorLine].Length)
        {
            _lines[_cursorLine] = _lines[_cursorLine].Remove(_cursorCol, 1);
        }
        else if (_cursorLine < _lines.Count - 1)
        {
            _lines[_cursorLine] += _lines[_cursorLine + 1];
            _lines.RemoveAt(_cursorLine + 1);
        }
    }

    private async Task<Navigation> HandleSendAsync()
    {
        var json = string.Join('\n', _lines).Trim();
        var paramsJson = json.Length > 0 ? json : null;

        var error = await _store.DispatchMessageAsync(_method, paramsJson, _isNotification);
        if (error is not null)
        {
            // Brief error display — could be improved with a flash message system
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            await Task.Delay(2000);
            return new Navigation.Stay();
        }

        // Pop back to the message list (skip any intermediate views like detail/picker)
        return new Navigation.PopTo(typeof(MessageListView));
    }
}
