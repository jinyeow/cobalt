using Cobalt.Core.Ado;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Bridges <see cref="GitApi"/> to the PR view-model interfaces. The signed-in
/// user id (needed for reviewer/creator filters and votes) is resolved lazily and
/// cached, so constructing the adapter never blocks on the network.
/// </summary>
public sealed class PullRequestStoreAdapter(GitApi api, Func<CancellationToken, Task<Guid>> resolveMe)
    : IPullRequestSource, IPullRequestStore
{
    private Guid? _me;

    private async Task<Guid> MeAsync(CancellationToken ct) =>
        _me ??= await resolveMe(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
        await api.ListPullRequestsAsync(filter, await MeAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

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
}
