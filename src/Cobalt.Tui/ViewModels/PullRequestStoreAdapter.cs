using Cobalt.Core.Ado;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Bridges <see cref="GitApi"/> to the PR view-model interfaces. The signed-in
/// user id (needed for reviewer/creator filters and votes) is resolved lazily and
/// cached, so constructing the adapter never blocks on the network.
/// </summary>
public sealed class PullRequestStoreAdapter(GitApi api, Func<CancellationToken, Task<Guid>> resolveMe)
    : IPullRequestSource, IPullRequestStore, IPrDiffSource
{
    private Guid? _me;

    private async Task<Guid> MeAsync(CancellationToken ct) =>
        _me ??= await resolveMe(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prs = await api.ListPullRequestsAsync(filter, me, ct).ConfigureAwait(false);

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

    public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) =>
        api.GetPullRequestAsync(id, ct);

    public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repositoryId, int id, CancellationToken ct) =>
        api.GetThreadsAsync(repositoryId, id, ct);

    public async Task VoteAsync(string repositoryId, int id, PrVote vote, CancellationToken ct) =>
        await api.VoteAsync(repositoryId, id, await MeAsync(ct).ConfigureAwait(false), vote, ct).ConfigureAwait(false);

    public Task ReplyToThreadAsync(string repositoryId, int id, int threadId, string text, CancellationToken ct) =>
        api.ReplyToThreadAsync(repositoryId, id, threadId, text, ct);

    public Task SetThreadStatusAsync(string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct) =>
        api.SetThreadStatusAsync(repositoryId, id, threadId, status, ct);

    public Task AbandonAsync(string repositoryId, int id, CancellationToken ct) =>
        api.AbandonAsync(repositoryId, id, ct);

    public async Task CompleteAsync(string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct)
    {
        // Complete requires the tip commit of the source branch as a concurrency guard.
        var pr = await api.GetPullRequestAsync(id, ct).ConfigureAwait(false);
        if (pr.LastMergeSourceCommitId is null)
        {
            throw new InvalidOperationException("cannot complete: the PR has no source commit (still computing merge?)");
        }
        await api.CompleteAsync(repositoryId, id, pr.LastMergeSourceCommitId, mergeStrategy, deleteSource, ct)
            .ConfigureAwait(false);
    }

    // ---- IPrDiffSource ----

    public Task<PrIteration?> GetLatestIterationAsync(string repositoryId, int prId, CancellationToken ct) =>
        api.GetLatestIterationAsync(repositoryId, prId, ct);

    public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string repositoryId, int prId, int iterationId, CancellationToken ct) =>
        api.GetIterationChangesAsync(repositoryId, prId, iterationId, ct);

    public Task<string> GetFileContentAsync(string repositoryId, string path, string commit, CancellationToken ct) =>
        api.GetFileContentAsync(repositoryId, path, commit, ct);

    Task<IReadOnlyList<PrThread>> IPrDiffSource.GetThreadsAsync(string repositoryId, int prId, CancellationToken ct) =>
        api.GetThreadsAsync(repositoryId, prId, ct);

    public async Task AddLineCommentAsync(string repositoryId, int prId, string path, int line, bool rightSide, string text, CancellationToken ct) =>
        await api.AddLineCommentAsync(repositoryId, prId, path, line, rightSide, text, ct).ConfigureAwait(false);
}
