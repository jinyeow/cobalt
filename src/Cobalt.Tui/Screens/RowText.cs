namespace Cobalt.Tui.Screens;

/// <summary>
/// Column primitives shared by the width-aware list formatters: fixed-width cells
/// (truncate with <c>…</c>, pad to fill) and a final row clamp that guarantees a
/// row is exactly <c>width</c> cells so the selection highlight spans it.
/// </summary>
internal static class RowText
{
    /// <summary>A fixed-width cell: truncated with an ellipsis when too long, right-padded when short.</summary>
    public static string Fit(string value, int width)
    {
        if (width <= 0)
        {
            return "";
        }
        if (value.Length <= width)
        {
            return value.PadRight(width);
        }
        return value[..(width - 1)] + "…";
    }

    /// <summary>Truncate to <c>width</c> only when longer; otherwise pad to fill it exactly.</summary>
    public static string TitleCell(string value, int width) => Fit(value, width);

    /// <summary>Force a completed row to exactly <c>width</c> cells (pad, or trim without splitting a surrogate pair).</summary>
    public static string Clamp(string row, int width)
    {
        if (width <= 0)
        {
            return "";
        }
        if (row.Length <= width)
        {
            return row.PadRight(width);
        }
        var cut = width;
        if (char.IsHighSurrogate(row[cut - 1]))
        {
            cut--; // never cut a surrogate pair in half
        }
        return row[..cut];
    }
}
