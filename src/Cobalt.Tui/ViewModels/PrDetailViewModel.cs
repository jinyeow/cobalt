using Cobalt.Core.Ado;
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
    public int Id => id;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public PullRequest? PullRequest { get; private set; }
    public IReadOnlyList<PrThread> Threads { get; private set; } = [];

    public int UnresolvedThreadCount =>
        Threads.Count(t => t.Status == PrThreadStatus.Active && !t.IsSystemOnly);

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            PullRequest = await store.GetPullRequestAsync(id, ct).ConfigureAwait(false);
            Threads = await store.GetThreadsAsync(
                PullRequest.ProjectName, PullRequest.RepositoryId, id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
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
            await action(project, repo).ConfigureAwait(false);
            if (reload)
            {
                PullRequest = await store.GetPullRequestAsync(id, ct).ConfigureAwait(false);
                Threads = await store.GetThreadsAsync(project, repo, id, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
