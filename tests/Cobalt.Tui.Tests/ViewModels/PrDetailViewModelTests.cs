using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrDetailViewModelTests
{
    private static PullRequest Pr() =>
        new(10, "Add feature", "the description", "active", false, "feature", "main", "succeeded",
            "Jin", "repo-1", "web", [new PrReviewer("r1", "Sam", PrVote.NoVote, true)], [101], "abc123",
            "Contoso.Web");

    private sealed class FakeStore : IPullRequestStore
    {
        public PullRequest Pr { get; set; } = PrDetailViewModelTests.Pr();
        public List<PrThread> Threads { get; set; } = [];
        public PrVote? VotedValue { get; private set; }
        public (int thread, string text)? Replied { get; private set; }
        public (int thread, PrThreadStatus status)? StatusSet { get; private set; }
        public bool Abandoned { get; private set; }
        public string? LastProject { get; private set; }

        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) => Task.FromResult(Pr);
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int id, CancellationToken ct)
        {
            LastProject = project;
            return Task.FromResult<IReadOnlyList<PrThread>>(Threads);
        }

        public Task VoteAsync(string project, string repo, int id, PrVote vote, CancellationToken ct)
        {
            LastProject = project;
            VotedValue = vote;
            return Task.CompletedTask;
        }

        public Task ReplyToThreadAsync(string project, string repo, int id, int threadId, string text, CancellationToken ct)
        {
            LastProject = project;
            Replied = (threadId, text);
            return Task.CompletedTask;
        }

        public Task SetThreadStatusAsync(string project, string repo, int id, int threadId, PrThreadStatus status, CancellationToken ct)
        {
            LastProject = project;
            StatusSet = (threadId, status);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(string project, string repo, int id, CancellationToken ct)
        {
            LastProject = project;
            Abandoned = true;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string project, string repo, int id, string mergeStrategy, bool deleteSource, CancellationToken ct)
        {
            LastProject = project;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Load_Fetches_Pr_And_Threads()
    {
        var store = new FakeStore
        {
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "fix this", false)], null, null, null)],
        };
        var vm = new PrDetailViewModel(store, 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Add feature", vm.PullRequest?.Title);
        Assert.Single(vm.Threads);
    }

    [Fact]
    public async Task Vote_Sends_Value_And_Refreshes()
    {
        var store = new FakeStore();
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.VoteAsync(PrVote.Approved, TestContext.Current.CancellationToken);

        Assert.Equal(PrVote.Approved, store.VotedValue);
    }

    [Fact]
    public async Task Reply_Passes_Thread_And_Text()
    {
        var store = new FakeStore
        {
            Threads = [new PrThread(7, PrThreadStatus.Active, [new PrComment(1, "Sam", "q", false)], null, null, null)],
        };
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ReplyAsync(7, "answered", TestContext.Current.CancellationToken);

        Assert.Equal((7, "answered"), store.Replied);
    }

    [Fact]
    public async Task ResolveThread_Sets_Fixed_Status()
    {
        var store = new FakeStore
        {
            Threads = [new PrThread(7, PrThreadStatus.Active, [new PrComment(1, "Sam", "q", false)], null, null, null)],
        };
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ResolveThreadAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal((7, PrThreadStatus.Fixed), store.StatusSet);
    }

    [Fact]
    public async Task ReactivateThread_Sets_Active_Status()
    {
        var store = new FakeStore
        {
            Threads = [new PrThread(7, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "q", false)], null, null, null)],
        };
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ReactivateThreadAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal((7, PrThreadStatus.Active), store.StatusSet);
    }

    [Fact]
    public async Task Abandon_Calls_Store()
    {
        var store = new FakeStore();
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.AbandonAsync(TestContext.Current.CancellationToken);

        Assert.True(store.Abandoned);
    }

    [Fact]
    public async Task Actions_Thread_The_Prs_Own_Project_Through()
    {
        var store = new FakeStore();
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.VoteAsync(PrVote.Approved, TestContext.Current.CancellationToken);

        // The PR belongs to "Contoso.Web" (not the ambient context project).
        Assert.Equal("Contoso.Web", store.LastProject);
    }

    [Fact]
    public async Task UnresolvedThreadCount_Counts_Active_NonSystem()
    {
        var store = new FakeStore
        {
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "a", "x", false)], null, null, null),
                new PrThread(2, PrThreadStatus.Fixed, [new PrComment(1, "a", "y", false)], null, null, null),
            ],
        };
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, vm.UnresolvedThreadCount);
    }
}
