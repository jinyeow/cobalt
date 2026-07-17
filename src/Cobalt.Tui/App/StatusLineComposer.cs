namespace Cobalt.Tui.App;

/// <summary>
/// Composes the status row: the view-model's status text on the left and the key
/// router's pending display (vim showcmd) right-aligned, with a trailing space so
/// the indicator doesn't touch the terminal edge. Pure string building.
/// </summary>
public static class StatusLineComposer
{
    public static string Compose(string left, string pending, int width)
    {
        if (pending.Length == 0)
        {
            return left;
        }

        var right = pending + " ";
        if (right.Length >= width)
        {
            return Screens.RowText.Clamp(right, width);
        }

        // RowText owns the shared truncate-or-pad cell semantics (ellipsis on cut,
        // surrogate-safe) — the status row must not grow a second implementation.
        return Screens.RowText.Fit(left, width - right.Length) + right;
    }
}
