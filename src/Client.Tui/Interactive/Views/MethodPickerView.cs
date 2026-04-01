using ManualLspClient.Core.MetaModel;
using ManualLspClient.Tui.Interactive.Framework;
using Spectre.Console;
using System.Text;

namespace ManualLspClient.Tui.Interactive.Views;

/// <summary>
/// Custom key-driven method picker view. Displays categorized LSP methods
/// and allows selecting one or typing a custom method name.
/// </summary>
public class MethodPickerView : ITuiView
{
    private const string CustomMethodLabel = "Custom method...";

    private readonly TuiStore _store;
    private readonly List<PickerRow> _rows = [];
    private readonly List<int> _selectableIndices = [];
    private readonly HashSet<string> _notificationMethods;

    private int _cursor;
    private readonly StringBuilder _customInput = new();

    public MethodPickerView(TuiStore store)
    {
        _store = store;
        _notificationMethods = store.MetaModel.GetClientNotifications()
            .Select(n => n.Method)
            .ToHashSet(StringComparer.Ordinal);
        BuildPickerRows();
    }

    public void OnEnter(object? args)
    {
        _cursor = 0;
        _customInput.Clear();
    }

    public void Render(RenderContext ctx)
    {
        bool onCustom = _selectableIndices[_cursor] == 0;
        int linesRendered = RenderPicker(ctx, onCustom ? _customInput.ToString() : null);
        ctx.ClearRemainingLines(linesRendered);
    }

    public Task<Navigation> HandleKeyAsync(ConsoleKeyInfo key)
    {
        bool onCustom = _selectableIndices[_cursor] == 0;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                if (_cursor > 0) _cursor--;
                break;

            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                if (_cursor < _selectableIndices.Count - 1) _cursor++;
                break;

            case ConsoleKey.Enter:
                if (onCustom)
                {
                    var name = _customInput.ToString().Trim();
                    if (name.Length == 0) break;
                    return Task.FromResult<Navigation>(
                        new Navigation.Replace(typeof(ParamsEditorView),
                            new ParamsEditorArgs(name, IsCustom: true)));
                }
                else
                {
                    var method = _rows[_selectableIndices[_cursor]].Label;
                    return Task.FromResult<Navigation>(
                        new Navigation.Replace(typeof(ParamsEditorView),
                            new ParamsEditorArgs(method, IsCustom: false)));
                }

            case ConsoleKey.Escape:
                return Task.FromResult<Navigation>(new Navigation.Pop());

            case ConsoleKey.Backspace:
                if (onCustom && _customInput.Length > 0)
                    _customInput.Length--;
                break;

            default:
                if (onCustom && !char.IsControl(key.KeyChar))
                    _customInput.Append(key.KeyChar);
                break;
        }

        return Task.FromResult<Navigation>(new Navigation.Stay());
    }

    public IReadOnlyList<FooterHint> GetFooterHints() =>
    [
        new("Enter", "Select"),
        new("↑/↓", "Move"),
        new("Esc", "Cancel"),
    ];

    // ── Rendering ──

    private int RenderPicker(RenderContext ctx, string? customInput)
    {
        int linesRendered = 0;

        int selectedRowIdx = _selectableIndices[_cursor];
        int availableLines = ctx.AvailableLines;

        // Determine scroll window centered on the selected row.
        // Reserve lines for the "↑ more" / "↓ more" indicators so we
        // never exceed availableLines total.
        int startRow = 0;
        int endRow = _rows.Count;
        if (_rows.Count > availableLines)
        {
            int half = availableLines / 2;
            startRow = Math.Max(0, selectedRowIdx - half);
            endRow = Math.Min(_rows.Count, startRow + availableLines);
            if (endRow == _rows.Count)
                startRow = Math.Max(0, endRow - availableLines);

            // Shrink the window to leave room for indicators
            bool hasUpIndicator = startRow > 0;
            bool hasDownIndicator = endRow < _rows.Count;
            int indicatorLines = (hasUpIndicator ? 1 : 0) + (hasDownIndicator ? 1 : 0);
            if (indicatorLines > 0)
            {
                int dataLines = availableLines - indicatorLines;
                half = dataLines / 2;
                startRow = Math.Max(0, selectedRowIdx - half);
                endRow = Math.Min(_rows.Count, startRow + dataLines);
                if (endRow == _rows.Count)
                    startRow = Math.Max(0, endRow - dataLines);
            }
        }

        if (startRow > 0)
        {
            ctx.WriteLinePadded("  [dim]↑ more[/]", 8);
            linesRendered++;
        }

        for (int i = startRow; i < endRow; i++)
        {
            var row = _rows[i];
            if (row.IsHeader)
            {
                ctx.WriteLinePadded($"  [blue]{Markup.Escape(row.Label)}[/]", row.Label.Length + 2);
            }
            else
            {
                bool isSelected = i == selectedRowIdx;
                var selector = isSelected ? "[bold cyan]>[/]" : " ";

                if (i == 0) // Custom method row
                {
                    if (isSelected && customInput is not null)
                    {
                        if (customInput.Length > 0)
                            ctx.WriteLinePadded($"  {selector} [bold]Custom: {Markup.Escape(customInput)}█[/]", customInput.Length + 14);
                        else
                            ctx.WriteLinePadded($"  {selector} [bold]Custom method[/] [dim](type method name)█[/]", 40);
                    }
                    else
                    {
                        var highlight = isSelected ? "bold" : "dim";
                        ctx.WriteLinePadded($"  {selector} [{highlight}]Custom method...[/]", 22);
                    }
                }
                else
                {
                    var highlight = isSelected ? "bold" : "dim";
                    ctx.WriteLinePadded($"  {selector} [{highlight}]{Markup.Escape(row.Label)}[/]", row.Label.Length + 5);
                }
            }
            linesRendered++;
        }

        if (endRow < _rows.Count)
        {
            ctx.WriteLinePadded("  [dim]↓ more[/]", 8);
            linesRendered++;
        }

        return linesRendered;
    }

    // ── Data ──

    private void BuildPickerRows()
    {
        var metaModel = _store.MetaModel;
        var requestMethods = metaModel.GetClientRequests().Select(r => r.Method);
        var notificationMethods = metaModel.GetClientNotifications().Select(n => n.Method);
        var allMethods = requestMethods.Concat(notificationMethods).Distinct().ToList();
        var grouped = RequestTemplateGenerator.GroupMethodsByCategory(allMethods);

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

    private record PickerRow(string Label, bool IsHeader);
}
