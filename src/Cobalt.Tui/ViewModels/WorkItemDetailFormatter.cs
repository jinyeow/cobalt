namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Composes the work-item detail body text for both the modal dialog (Full tier) and
/// the list-preview pane (Summary tier) — the single text composition ADR 0024
/// mandates ("no second formatter"). Pure and UI-free (ADR 0004). Full output is
/// width-independent (the consuming view word-wraps); the width shapes only the
/// Summary tier, which truncates the unbounded sections and clamps lines.
/// </summary>
public static class WorkItemDetailFormatter
{
    public static string Render(WorkItemDetailViewModel vm, int width, PreviewTier tier)
    {
        if (vm.IsLoading)
        {
            return "loading…";
        }
        var item = vm.Item;
        if (item is null)
        {
            return vm.Error is { } e ? $"error: {e}" : "no data";
        }

        var lines = new List<string>
        {
            $"{item.WorkItemType} #{item.Id}   [{item.State}]",
            $"Title:    {item.Title}",
            $"Assigned: {item.AssignedToDisplayName ?? "(unassigned)"}",
            $"Iteration:{item.IterationPath}",
            $"Tags:     {string.Join(", ", item.Tags)}",
            $"Priority: {item.Priority?.ToString() ?? "-"}   Points: {item.StoryPoints?.ToString() ?? "-"}",
            "",
            "── Description ──" + (vm.DescriptionLossy ? "  ⚠ rich HTML: editing may drop formatting" : ""),
        };
        if (vm.DescriptionMarkdown.Length == 0)
        {
            lines.Add("(empty)");
        }
        else if (tier == PreviewTier.Full)
        {
            lines.Add(vm.DescriptionMarkdown);
        }
        else
        {
            AddHead(lines, vm.DescriptionMarkdown);
        }
        lines.Add("");
        lines.Add($"── Comments ({vm.Comments.Count}) ──");
        // Summary keeps the latest comments (the list renders oldest-first).
        var comments = tier == PreviewTier.Full ? vm.Comments : [.. vm.Comments.TakeLast(SummaryComments)];
        lines.AddRange(comments.Select(c => $"  {c.Author} ({c.CreatedDate:yyyy-MM-dd}): {c.TextMarkdown}"));
        if (vm.Error is { } err)
        {
            lines.Add("");
            lines.Add($"error: {err}");
        }
        var text = string.Join('\n', lines);
        // Summary output is '\n'-normalized (DescriptionMarkdown carries platform newlines).
        return tier == PreviewTier.Summary ? ClampLines(text.ReplaceLineEndings("\n"), width) : text;
    }

    private static string ClampLines(string text, int width)
    {
        if (width <= 0)
        {
            return text;
        }
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > width)
            {
                var cut = width - 1;
                // Never end the cut on the high half of a surrogate pair: keeping it
                // would leave an invalid lone surrogate before the ellipsis.
                if (cut > 0 && char.IsHighSurrogate(lines[i][cut - 1]))
                {
                    cut--;
                }
                lines[i] = string.Concat(lines[i].AsSpan(0, cut), "…");
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>Summary tier: at most this many description lines before an ellipsis line.</summary>
    private const int SummaryHeadLines = 3;

    /// <summary>Summary tier: at most this many (latest) comments.</summary>
    private const int SummaryComments = 2;

    private static void AddHead(List<string> lines, string body)
    {
        var bodyLines = body.ReplaceLineEndings("\n").Split('\n');
        if (bodyLines.Length <= SummaryHeadLines)
        {
            lines.AddRange(bodyLines);
            return;
        }
        lines.AddRange(bodyLines.Take(SummaryHeadLines));
        lines.Add("…");
    }
}
