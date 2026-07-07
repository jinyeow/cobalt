using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Bridges <see cref="GitApi"/> to the PR view-model interfaces. The signed-in
/// user id (needed for reviewer/creator filters and votes) is resolved lazily and
/// cached, so constructing the adapter never blocks on the network.
///
/// <para><see cref="Scope"/> is mutable so the <c>:scope</c> palette command can flip
/// the PR lists between the whole org and a single project at runtime. Every
/// repo-scoped call threads the selected PR's own project through, so drill-in
/// (threads/vote/diff/URLs) stays correct even when rows span projects.</para>
/// </summary>
public sealed class PullRequestStoreAdapter(
    GitApi api,
    Func<CancellationToken, Task<Guid>> resolveMe,
    Func<CancellationToken, Task<TeamDirectory>>? resolveTeams = null,
    string project = "",
    PrScope initialScope = PrScope.Org)
    : IPullRequestSource, IPullRequestStore, IPrDiffSource
{
    private Guid? _me;
    private TeamDirectory? _teams;

    /// <summary>The active PR-list breadth; flipped by the <c>:scope</c> command.</summary>
    public PrScope Scope { get; set; } = initialScope;

    private async Task<Guid> MeAsync(CancellationToken ct) =>
        _me ??= await resolveMe(ct).ConfigureAwait(false);

    private async Task<TeamDirectory> TeamsAsync(CancellationToken ct) =>
        _teams ??= await (resolveTeams
            ?? throw new InvalidOperationException("no team directory resolver configured"))(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct)
    {
        if (filter == PrListFilter.Team)
        {
            return await ListTeamAsync(ct).ConfigureAwait(false);
        }

        var me = await MeAsync(ct).ConfigureAwait(false);
        var prs = await api.ListPullRequestsAsync(filter, me, Scope, ct).ConfigureAwait(false);

        // The review queue is "PRs awaiting MY vote" (SPEC §3): ADO has no server-side
        // "my vote pending" filter, so drop the ones I've already voted on.
        if (filter == PrListFilter.ReviewQueue)
        {
            var meId = me.ToString();
            prs =
            [
                .. prs.Where(pr => pr.Reviewers
                    .Where(r => string.Equals(r.Id, meId, StringComparison.OrdinalIgnoreCase))
                    .All(r => r.Vote == PrVote.NoVote)),
            ];
        }
        return prs;
    }

    /// <summary>
    /// The Team tab is the RAW UNION of (a) PRs where a team the user belongs to is a
    /// requested reviewer and (b) PRs authored by a teammate — deduped by
    /// <see cref="PullRequest.PullRequestId"/> only (ADR 0015). The user's own PRs and PRs
    /// already shown on other tabs are intentionally NOT excluded.
    /// </summary>
    private async Task<IReadOnlyList<PullRequest>> ListTeamAsync(CancellationToken ct)
    {
        var directory = await TeamsAsync(ct).ConfigureAwait(false);

        // Under project scope, only teams that live in the context project take part.
        var teams = Scope == PrScope.Project
            ? directory.Teams.Where(t => string.Equals(t.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            : directory.Teams;
        var inScope = teams.ToList();

        var teammateIds = inScope.SelectMany(t => t.MemberIds).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // (a) team-as-reviewer: one reviewer-list call per in-scope team, in parallel.
        var reviewerTask = Task.WhenAll(inScope.Select(t =>
            api.ListPullRequestsForReviewerAsync(t.TeamId, Scope, ct)));
        // (b) teammate-authored: reuse the single Active list, filtered client-side by author.
        var activeTask = api.ListPullRequestsAsync(PrListFilter.Active, Guid.Empty, Scope, ct);

        var reviewed = await reviewerTask.ConfigureAwait(false);
        var active = await activeTask.ConfigureAwait(false);

        var union = new Dictionary<int, PullRequest>();
        foreach (var pr in reviewed.SelectMany(r => r))
        {
            union[pr.PullRequestId] = pr;
        }
        foreach (var pr in active.Where(pr => teammateIds.Contains(pr.AuthorId)))
        {
            union.TryAdd(pr.PullRequestId, pr);
        }

        return [.. union.Values.OrderByDescending(pr => pr.CreationDate ?? DateTimeOffset.MinValue)];
    }

    public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) =>
        api.GetPullRequestAsync(id, ct);

    public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int id, CancellationToken ct) =>
        api.GetThreadsAsync(repositoryId, id, project, ct);

    public async Task VoteAsync(string project, string repositoryId, int id, PrVote vote, CancellationToken ct) =>
        await api.VoteAsync(repositoryId, id, await MeAsync(ct).ConfigureAwait(false), vote, project, ct).ConfigureAwait(false);

    public Task ReplyToThreadAsync(string project, string repositoryId, int id, int threadId, string text, CancellationToken ct) =>
        api.ReplyToThreadAsync(repositoryId, id, threadId, text, project, ct);

    public Task SetThreadStatusAsync(string project, string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct) =>
        api.SetThreadStatusAsync(repositoryId, id, threadId, status, project, ct);

    public Task AbandonAsync(string project, string repositoryId, int id, CancellationToken ct) =>
        api.AbandonAsync(repositoryId, id, project, ct);

    public async Task CompleteAsync(string project, string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct)
    {
        // Complete requires the tip commit of the source branch as a concurrency guard.
        var pr = await api.GetPullRequestAsync(id, ct).ConfigureAwait(false);
        if (pr.LastMergeSourceCommitId is null)
        {
            throw new InvalidOperationException("cannot complete: the PR has no source commit (still computing merge?)");
        }
        await api.CompleteAsync(repositoryId, id, pr.LastMergeSourceCommitId, mergeStrategy, deleteSource, project, ct)
            .ConfigureAwait(false);
    }

    // Stubs pending the PR-detail track (Phase 0 contract lock only — no behavior).
    public Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct) =>
        throw new NotImplementedException("provided by PR-detail track");

    public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string project, int id, CancellationToken ct) =>
        throw new NotImplementedException("provided by PR-detail track");

    // ---- IPrDiffSource ----

    public Task<PrIteration?> GetLatestIterationAsync(string project, string repositoryId, int prId, CancellationToken ct) =>
        api.GetLatestIterationAsync(repositoryId, prId, project, ct);

    public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repositoryId, int prId, int iterationId, CancellationToken ct) =>
        api.GetIterationChangesAsync(repositoryId, prId, iterationId, project, ct);

    public Task<string> GetFileContentAsync(string project, string repositoryId, string path, string commit, CancellationToken ct) =>
        api.GetFileContentAsync(repositoryId, path, commit, project, ct);

    Task<IReadOnlyList<PrThread>> IPrDiffSource.GetThreadsAsync(string project, string repositoryId, int prId, CancellationToken ct) =>
        api.GetThreadsAsync(repositoryId, prId, project, ct);

    public async Task AddLineCommentAsync(string project, string repositoryId, int prId, string path, int line, bool rightSide, string text, CancellationToken ct) =>
        await api.AddLineCommentAsync(repositoryId, prId, path, line, rightSide, text, project, ct).ConfigureAwait(false);
}
