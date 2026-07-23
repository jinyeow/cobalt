namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The preview pane's vertical budget (#48). The detail formatters clamp Summary output
/// horizontally but are vertically unbounded, so the pane caps the rendered text to the
/// lines it can actually show and says how many were dropped. Pure and UI-free (ADR 0004);
/// deliberately outside the formatters, whose Full-tier output is snapshot-pinned.
/// </summary>
public static class PreviewBudget
{
    /// <summary>
    /// Caps <paramref name="text"/> to <paramref name="maxLines"/> rendered lines, replacing the
    /// overflow with a trailing "… N more" marker. A non-positive budget means "no budget known"
    /// (an unlaid-out view) and returns the text unchanged.
    /// </summary>
    public static string Fit(string text, int maxLines)
    {
        if (maxLines <= 0)
        {
            return text;
        }
        var lines = text.Split('\n');
        if (lines.Length <= maxLines)
        {
            return text;
        }
        var head = maxLines - 1;
        return string.Join('\n', lines.Take(head).Append($"… {lines.Length - head} more"));
    }
}
