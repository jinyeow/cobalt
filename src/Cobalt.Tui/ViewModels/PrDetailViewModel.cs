using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

public interface IPullRequestStore
{
    Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repositoryId, int id, CancellationToken ct);
    Task VoteAsync(string repositoryId, int id, PrVote vote, CancellationToken ct);
    Task ReplyToThreadAsync(string repositoryId, int id, int threadId, string text, CancellationToken ct);
    Task SetThreadStatusAsync(string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct);
    Task AbandonAsync(string repositoryId, int id, CancellationToken ct);
    Task CompleteAsync(string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct);
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
            Threads = await store.GetThreadsAsync(PullRequest.RepositoryId, id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public Task VoteAsync(PrVote vote, CancellationToken ct) =>
        RunAsync(repo => store.VoteAsync(repo, id, vote, ct), ct);

    public Task ReplyAsync(int threadId, string text, CancellationToken ct) =>
        RunAsync(repo => store.ReplyToThreadAsync(repo, id, threadId, text, ct), ct);

    public Task ResolveThreadAsync(int threadId, CancellationToken ct) =>
        RunAsync(repo => store.SetThreadStatusAsync(repo, id, threadId, PrThreadStatus.Fixed, ct), ct);

    public Task ReactivateThreadAsync(int threadId, CancellationToken ct) =>
        RunAsync(repo => store.SetThreadStatusAsync(repo, id, threadId, PrThreadStatus.Active, ct), ct);

    public Task AbandonAsync(CancellationToken ct) =>
        RunAsync(repo => store.AbandonAsync(repo, id, ct), ct, reload: false);

    public Task CompleteAsync(string mergeStrategy, bool deleteSource, CancellationToken ct) =>
        RunAsync(repo => store.CompleteAsync(repo, id, mergeStrategy, deleteSource, ct), ct, reload: false);

    private async Task RunAsync(Func<string, Task> action, CancellationToken ct, bool reload = true)
    {
        if (PullRequest is null)
        {
            return;
        }
        var repo = PullRequest.RepositoryId;
        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await action(repo).ConfigureAwait(false);
            if (reload)
            {
                PullRequest = await store.GetPullRequestAsync(id, ct).ConfigureAwait(false);
                Threads = await store.GetThreadsAsync(repo, id, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
