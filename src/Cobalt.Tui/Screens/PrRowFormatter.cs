using Cobalt.Core.Models;
using Cobalt.Core.Text;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Derived column widths for a PR result set: the repo column is sized to the
/// longest name present (capped), and the project column only appears when the
/// rows actually span more than one project (the org-scoped, cross-project case).
/// </summary>
internal readonly record struct PrColumns(int RepoWidth, bool ShowProject, int ProjectWidth)
{
    public const int MaxRepoWidth = 30;
    public const int MaxProjectWidth = 20;

    public static PrColumns For(IReadOnlyList<PullRequest> rows)
    {
        if (rows.Count == 0)
        {
            return new PrColumns(0, false, 0);
        }
        var repoWidth = Math.Min(MaxRepoWidth, rows.Max(r => r.RepositoryName.Length));
        var projects = rows.Select(r => r.ProjectName).Where(p => p.Length > 0).Distinct().ToList();
        var showProject = projects.Count > 1;
        var projectWidth = showProject ? Math.Min(MaxProjectWidth, projects.Max(p => p.Length)) : 0;
        return new PrColumns(repoWidth, showProject, projectWidth);
    }
}

/// <summary>
/// Pure, width-aware row formatting for the PR list. Fixed columns (id, votes, age,
/// optional project, repo) sit left; the title takes all remaining width; an optional
/// comment badge trails at the right. "Now" is injected so the age column is
/// deterministic. Every row is exactly <c>width</c> cells.
/// </summary>
internal static class PrRowFormatter
{
    private const int IdWidth = 6;   // "!12345"
    private const int VoteWidth = 6; // "⧗ wait"
    private const int AgeWidth = 3;  // "45m", "12d", "52w"

    public static string Format(PullRequest pr, int width, PrColumns cols, DateTimeOffset now, int? comments)
    {
        if (width <= 0)
        {
            return "";
        }

        var left =
            RowText.Fit("!" + pr.PullRequestId, IdWidth) + " " +
            RowText.Fit(VoteSummary(pr), VoteWidth) + " " +
            AgeFormat.Since(pr.CreationDate, now).PadLeft(AgeWidth) + " " +
            (cols.ShowProject ? RowText.Fit(pr.ProjectName, cols.ProjectWidth) + " " : "") +
            RowText.Fit(pr.RepositoryName, cols.RepoWidth) + " " +
            (pr.IsDraft ? "[draft] " : "");

        var badge = comments is int count ? $" 💬 {count}" : "";
        var titleSpace = Math.Max(0, width - left.Length - badge.Length);
        var row = left + RowText.TitleCell(pr.Title, titleSpace) + badge;
        return RowText.Clamp(row, width);
    }

    private static string VoteSummary(PullRequest pr)
    {
        if (pr.Reviewers.Any(r => r.Vote == PrVote.Rejected))
        {
            return "✗ rej";
        }
        if (pr.Reviewers.Any(r => r.Vote == PrVote.WaitingForAuthor))
        {
            return "⧗ wait";
        }
        var approved = pr.Reviewers.Count(r => r.Vote is PrVote.Approved or PrVote.ApprovedWithSuggestions);
        return approved > 0 ? $"✓ {approved}" : "·";
    }
}
