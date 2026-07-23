using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Composes the PR detail body text for both the modal dialog (Full tier) and the
/// list-preview pane (Summary tier) — the single text composition ADR 0024 mandates
/// ("no second formatter"). Pure and UI-free (ADR 0004). Full output is
/// width-independent (the consuming view word-wraps); the width shapes only the
/// Summary tier, which truncates the unbounded sections and clamps lines.
/// </summary>
public static class PrDetailFormatter
{
    public static string Render(PrDetailViewModel vm, int width, PreviewTier tier)
    {
        if (vm.IsLoading)
        {
            return "loading…";
        }
        var pr = vm.PullRequest;
        if (pr is null)
        {
            return vm.Error is { } e ? $"error: {e}" : "no data";
        }

        var lines = new List<string>
        {
            $"!{pr.PullRequestId}  {pr.Title}" + (pr.IsDraft ? "  [draft]" : ""),
            $"{pr.RepositoryName}: {pr.SourceBranch} → {pr.TargetBranch}   status: {pr.Status}   merge: {pr.MergeStatus ?? "?"}",
            $"author: {pr.Author}",
            "",
            "Reviewers:",
        };
        lines.AddRange(pr.Reviewers.Count == 0
            ? ["  (none)"]
            : pr.Reviewers.Select(r => $"  {VoteGlyph(r.Vote)} {r.DisplayName}{(r.IsRequired ? " (required)" : "")}"));

        if (vm.Policies.Count > 0)
        {
            lines.Add("");
            lines.Add("Policies:");
            lines.AddRange(vm.Policies.Select(p =>
                $"  {PolicyGlyph(p.Status)} {p.DisplayName}{(p.IsBlocking ? " (blocking)" : "")}"));
        }

        if (pr.LinkedWorkItemIds.Count > 0)
        {
            lines.Add("");
            lines.Add($"Linked work items: {string.Join(", ", pr.LinkedWorkItemIds.Select(i => $"#{i}"))}");
        }

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            lines.Add("");
            lines.Add("── Description ──");
            if (tier == PreviewTier.Full)
            {
                lines.Add(pr.Description!);
            }
            else
            {
                AddHead(lines, pr.Description!);
            }
        }

        lines.Add("");
        lines.Add($"── Threads ({vm.UnresolvedThreadCount} unresolved) ──");
        if (tier == PreviewTier.Full)
        {
            foreach (var t in vm.Threads)
            {
                var anchor = t.FilePath is null ? "" : $" [{t.FilePath}:{t.RightLine ?? t.LeftLine}]";
                lines.Add($"  #{t.Id} [{t.Status}]{anchor}");
                lines.AddRange(t.Comments.Where(c => !c.IsSystem).Select(c => $"      {c.Author}: {c.Content}"));
            }
        }

        if (vm.Error is { } err)
        {
            lines.Add("");
            lines.Add($"error: {err}");
        }
        var text = string.Join('\n', lines);
        // Summary output is '\n'-normalized (ADO descriptions can carry CRLF newlines).
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
                lines[i] = string.Concat(lines[i].AsSpan(0, width - 1), "…");
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>Summary tier: at most this many description lines before an ellipsis line.</summary>
    private const int SummaryHeadLines = 3;

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

    private static string VoteGlyph(PrVote vote) => vote switch
    {
        PrVote.Approved => "✓",
        PrVote.ApprovedWithSuggestions => "✓~",
        PrVote.WaitingForAuthor => "⧗",
        PrVote.Rejected => "✗",
        _ => "·",
    };

    private static string PolicyGlyph(string status) => status.ToLowerInvariant() switch
    {
        "approved" => "✓",
        "rejected" => "✗",
        _ => "⧗", // queued / running / notApplicable / etc.
    };
}
