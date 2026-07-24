namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The preview pane's vertical budget (#48). The detail formatters clamp Summary output
/// horizontally but are vertically unbounded, so the pane caps the rendered text and says how
/// many lines were dropped. Pure and UI-free (ADR 0004); deliberately outside the formatters,
/// whose Full-tier output is snapshot-pinned.
/// </summary>
public static class PreviewBudget
{
    /// <summary>
    /// The pane's hard content cap. Deliberately independent of the pane's height: budgeting to
    /// the visible rows would leave nothing off-screen, which would make scrolling — the pane's
    /// only verb (ADR 0024) — a no-op. Set well above any real Summary-tier detail, so it is a
    /// safety valve against pathological content rather than a routine truncation.
    /// </summary>
    public const int MaxLines = 500;

    /// <summary>
    /// Caps <paramref name="text"/> to <paramref name="maxLines"/> rendered lines, replacing the
    /// overflow with a trailing "… N more" marker. A non-positive budget means "no budget" and
    /// returns the text unchanged.
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
