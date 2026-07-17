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
    PrScope initialScope = PrScope.Org,
    PolicyApi? policy = null)
    : IPullRequestSource, IPullRequestStore, IPrDiffSource
{
    private Guid? _me;
    private readonly object _teamsLock = new();
    private Task<TeamDirectory>? _teamsInflight;

    /// <summary>The active PR-list breadth; flipped by the <c>:scope</c> command.</summary>
    public PrScope Scope { get; set; } = initialScope;

    private async Task<Guid> MeAsync(CancellationToken ct) =>
        _me ??= await resolveMe(ct).ConfigureAwait(false);

    /// <summary>
    /// The team directory, resolved once and shared. Single-flight and <em>start-detached</em>: the
    /// first caller starts the build; every caller awaits it via <see cref="Task.WaitAsync(CancellationToken)"/>,
    /// so one caller's cancellation cancels only its own await, never the shared build the others are
    /// joined to (ADR 0008). The shared build runs on <see cref="CancellationToken.None"/>, and its
    /// eviction is attached to the shared task (not any caller's await), so a build that ends
    /// unsuccessfully — faulted <em>or</em> canceled, e.g. an HttpClient timeout surfacing as a
    /// cancelled task — is evicted and retried rather than cached forever.
    /// </summary>
    private Task<TeamDirectory> TeamsAsync(CancellationToken ct)
    {
        Task<TeamDirectory> shared;
        lock (_teamsLock)
        {
            if (_teamsInflight is not { } existing)
            {
                existing = (resolveTeams ?? throw new InvalidOperationException("no team directory resolver configured"))(
                    CancellationToken.None);
                // Assign the field *before* attaching the eviction, so an already-completed build
                // (e.g. Task.FromCanceled from an HttpClient timeout) evicts itself right here via
                // the synchronous continuation instead of being cached before eviction can see it.
                _teamsInflight = existing;
                // Evict by identity the moment the shared build ends unsuccessfully (faulted OR
                // canceled), observing any fault so it never reaches the crash-log hook (ADR 0013).
                // Attached to the shared task, not a caller's WaitAsync, so a cancelled joiner still
                // leaves the build running for the others and a cancelled build is not cached poison.
                _ = existing.ContinueWith(
                    EvictIfUnsuccessful,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            // Local, so the synchronous eviction above nulling the field does not matter here.
            shared = existing;
        }
        return shared.WaitAsync(ct);
    }

    private void EvictIfUnsuccessful(Task<TeamDirectory> build)
    {
        if (build.IsCompletedSuccessfully)
        {
            return;
        }
        _ = build.Exception; // observe a fault (a canceled task carries none)
        lock (_teamsLock)
        {
            if (ReferenceEquals(_teamsInflight, build))
            {
                _teamsInflight = null;
            }
        }
    }

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
        // (b) teammate-authored: reuse the single Active list, filtered client-side by author.
        // Started before the directory is awaited so the two round-trips overlap; observed on the
        // fault path so a directory-build failure never leaves it as an unobserved orphan (ADR 0013).
        var activeTask = api.ListPullRequestsAsync(PrListFilter.Active, Guid.Empty, Scope, ct);
        try
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

            var reviewed = await reviewerTask.ConfigureAwait(false);
            var active = await activeTask.ConfigureAwait(false);

            return BuildTeamUnion(reviewed, active, teammateIds);
        }
        finally
        {
            ObserveFault(activeTask);
        }
    }

    private static IReadOnlyList<PullRequest> BuildTeamUnion(
        IReadOnlyList<PullRequest>[] reviewed, IReadOnlyList<PullRequest> active, HashSet<string> teammateIds)
    {
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

    // A PR-level comment is a new thread with NO threadContext (contrast AddLineCommentAsync,
    // which anchors the thread to a file/line via ThreadContext).
    public Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct) =>
        api.AddThreadAsync(repositoryId, id, new NewThreadRequest
        {
            Comments = [new NewCommentRequest { Content = text }],
            Status = 1, // active
        }, project, ct);

    // The project GUID (PullRequest.ProjectId) is threaded as the route + artifactId project
    // component; the CodeReviewId artifactId requires the GUID specifically.
    public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string projectId, int id, CancellationToken ct) =>
        (policy ?? throw new InvalidOperationException("no policy api configured")).GetEvaluationsAsync(projectId, id, ct);

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

    /// <summary>
    /// Observes a fetch left unawaited because something before its await threw (the Active list,
    /// when the team-directory build fails). Without this its fault resurfaces via the
    /// <see cref="TaskScheduler.UnobservedTaskException"/> hook as a phantom crash-log entry (ADR
    /// 0013). Harmless on an already-awaited task, so callers invoke it on every exit path.
    /// </summary>
    private static void ObserveFault(Task? task) =>
        _ = task?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
