using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

public interface IPullRequestStore
{
    Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int id, CancellationToken ct);
    Task VoteAsync(string project, string repositoryId, int id, PrVote vote, CancellationToken ct);
    Task ReplyToThreadAsync(string project, string repositoryId, int id, int threadId, string text, CancellationToken ct);
    Task SetThreadStatusAsync(string project, string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct);
    Task AbandonAsync(string project, string repositoryId, int id, CancellationToken ct);
    Task CompleteAsync(string project, string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct);
    Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct);
    Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string project, int id, CancellationToken ct);
}

/// <summary>
/// PR detail: reviewers/votes, merge status, comment threads, and the actions
/// (vote, reply, resolve/reactivate, complete, abandon). UI-free (ADR 0004).
/// </summary>
public sealed class PrDetailViewModel(IPullRequestStore store, int id)
{
    /// <summary>
    /// Seeds the view-model with a PR the caller already holds — the preview pane's tier 1
    /// (ADR 0024): the list row renders through the same formatter with zero fetches, and
    /// <see cref="LoadAsync"/> later replaces it with the full detail. Threads and policies stay
    /// empty until then; only the fetch knows them.
    /// </summary>
    public PrDetailViewModel(IPullRequestStore store, PullRequest row)
        : this(store, row.PullRequestId) => PullRequest = row;

    public int Id => id;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public PullRequest? PullRequest { get; private set; }
    public IReadOnlyList<PrThread> Threads { get; private set; } = [];
    public IReadOnlyList<PolicyEvaluation> Policies { get; private set; } = [];

    public int UnresolvedThreadCount =>
        Threads.Count(t => t.Status == PrThreadStatus.Active && !t.IsSystemOnly);

    /// <summary>
    /// Raised when the detail state changes. May fire on a threadpool continuation (an ADO
    /// load/mutation completing), so a subscriber that touches Terminal.Gui must marshal onto the UI
    /// thread via <see cref="App.IUiPost"/> — never <c>IApplication</c>, which this UI-free
    /// view-model (ADR 0004) deliberately does not reference.
    /// </summary>
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await VmGuard.RunAsync(async () =>
            {
                PullRequest = await store.GetPullRequestAsync(id, ct).ConfigureAwait(false);
                Threads = await store.GetThreadsAsync(
                    PullRequest.ProjectName, PullRequest.RepositoryId, id, ct).ConfigureAwait(false);
                // Branch policies are secondary: an expected ADO failure here surfaces via Error
                // (below) but leaves the already-loaded PR/threads intact rather than blanking the pane.
                Policies = await store.GetPolicyEvaluationsAsync(PullRequest.ProjectId, id, ct).ConfigureAwait(false);
            }, ct, m => Error = m).ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public Task VoteAsync(PrVote vote, CancellationToken ct) =>
        RunAsync((project, repo) => store.VoteAsync(project, repo, id, vote, ct), ct);

    public Task ReplyAsync(int threadId, string text, CancellationToken ct) =>
        RunAsync((project, repo) => store.ReplyToThreadAsync(project, repo, id, threadId, text, ct), ct);

    public Task AddPrCommentAsync(string text, CancellationToken ct) =>
        RunAsync((project, repo) => store.AddPrCommentAsync(project, repo, id, text, ct), ct);

    public Task ResolveThreadAsync(int threadId, CancellationToken ct) =>
        RunAsync((project, repo) => store.SetThreadStatusAsync(project, repo, id, threadId, PrThreadStatus.Fixed, ct), ct);

    public Task ReactivateThreadAsync(int threadId, CancellationToken ct) =>
        RunAsync((project, repo) => store.SetThreadStatusAsync(project, repo, id, threadId, PrThreadStatus.Active, ct), ct);

    public Task AbandonAsync(CancellationToken ct) =>
        RunAsync((project, repo) => store.AbandonAsync(project, repo, id, ct), ct, reload: false);

    public Task CompleteAsync(string mergeStrategy, bool deleteSource, CancellationToken ct)
    {
        // Complete needs the source branch's tip commit as a concurrency guard; while the
        // merge is still computing the PR has none. Detect that up front and surface a clear
        // error (H1) instead of letting the store throw into a discarded fire-and-forget task.
        if (PullRequest is { LastMergeSourceCommitId: null or "" })
        {
            Error = "cannot complete: merge still computing — try again in a moment";
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        return RunAsync((project, repo) => store.CompleteAsync(project, repo, id, mergeStrategy, deleteSource, ct), ct, reload: false);
    }

    private async Task RunAsync(Func<string, string, Task> action, CancellationToken ct, bool reload = true)
    {
        if (PullRequest is null)
        {
            return;
        }
        var project = PullRequest.ProjectName;
        var repo = PullRequest.RepositoryId;
        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await VmGuard.RunAsync(async () =>
            {
                await action(project, repo).ConfigureAwait(false);
                if (reload)
                {
                    PullRequest = await store.GetPullRequestAsync(id, ct).ConfigureAwait(false);
                    Threads = await store.GetThreadsAsync(project, repo, id, ct).ConfigureAwait(false);
                }
            }, ct, m => Error = m).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
