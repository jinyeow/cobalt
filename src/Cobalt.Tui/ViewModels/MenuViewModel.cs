namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The reusable menu component's state (ADR 0022 stage C): a fixed row list, an incremental
/// filter, a clamped selection, and the rendered row texts. Terminal.Gui-free (ADR 0004), so
/// every menu behaviour except key delivery is provable without a terminal.
/// </summary>
/// <remarks>
/// The filter is an order-preserving subsequence match over "&lt;hint&gt; &lt;label&gt;", not the
/// palette's prefix-first ranking: help rows are anchored by their key hints, and reordering on
/// each keystroke would make the hints jump under the user's eyes.
/// </remarks>
public sealed class MenuViewModel<T>
{
    private readonly IReadOnlyList<MenuOption<T>> _options;
    private int _selectedIndex;

    public MenuViewModel(IReadOnlyList<MenuOption<T>> options)
    {
        _options = options;
        VisibleOptions = options;
    }

    /// <summary>The rows the current filter admits, in the original row order.</summary>
    public IReadOnlyList<MenuOption<T>> VisibleOptions { get; private set; }

    /// <summary>The live filter text ("" when the filter bar has never been used).</summary>
    public string Filter { get; private set; } = "";

    /// <summary>The highlighted row's index within <see cref="VisibleOptions"/>, always clamped.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = VisibleOptions.Count == 0 ? 0 : Math.Clamp(value, 0, VisibleOptions.Count - 1);
    }

    /// <summary>The highlighted row, or null when the filter admits nothing.</summary>
    public MenuOption<T>? Selected =>
        _selectedIndex >= 0 && _selectedIndex < VisibleOptions.Count ? VisibleOptions[_selectedIndex] : null;

    /// <summary>
    /// Narrows the rows to those matching <paramref name="text"/> and puts the highlight back on
    /// the top match — so Enter straight after typing runs what the user is looking at.
    /// </summary>
    public void SetFilter(string text)
    {
        Filter = text;
        VisibleOptions = text.Length == 0
            ? _options
            : [.. _options.Where(o => FuzzyFilter.IsSubsequence(text, $"{o.KeyHint} {o.Label}"))];
        _selectedIndex = 0;
    }

    /// <summary>
    /// The visible rows as "  &lt;hint&gt; &lt;label&gt;", the hint column padded to the widest
    /// hint of *all* rows (never under the string cheatsheet's 8) so filtering never shifts the
    /// labels, each row truncated to <paramref name="width"/>.
    /// </summary>
    public IReadOnlyList<string> FormatRows(int width)
    {
        // Over ALL options, not the visible ones: a column measured on the filtered rows re-flows
        // every surviving label the moment the widest hint is filtered out.
        var hintColumn = Math.Max(8, _options.Count == 0 ? 0 : _options.Max(o => o.KeyHint.Length));
        return
        [
            .. VisibleOptions.Select(o =>
            {
                var row = $"  {o.KeyHint.PadRight(hintColumn)} {o.Label}";
                return row.Length <= width ? row : row[..Math.Max(0, width)];
            }),
        ];
    }
}
