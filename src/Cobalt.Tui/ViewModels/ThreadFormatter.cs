using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Renders a set of PR threads as the plain text both thread surfaces show: the diff dialog's
/// read-only thread view and <c>ThreadViewDialog</c>'s body. Pure formatting, so it lives here
/// rather than being duplicated in each screen (ADR 0004).
/// </summary>
internal static class ThreadFormatter
{
    /// <summary>
    /// One block per thread — an <c>#id [status]</c> header followed by its non-system comments —
    /// separated by a blank line. System comments are dropped because they are ADO bookkeeping
    /// ("voted approve", "updated the PR"), not review conversation.
    /// </summary>
    public static string Format(IReadOnlyList<PrThread> threads)
    {
        var lines = new List<string>();
        foreach (var thread in threads)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }
            lines.Add($"#{thread.Id} [{thread.Status}]");
            lines.AddRange(thread.Comments
                .Where(c => !c.IsSystem)
                .Select(c => $"  {c.Author}: {c.Content}"));
        }
        return string.Join('\n', lines);
    }
}
