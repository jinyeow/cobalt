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
            return right[..Math.Max(0, width)];
        }

        var available = width - right.Length;
        var head = left.Length > available ? left[..available] : left.PadRight(available);
        return head + right;
    }
}
