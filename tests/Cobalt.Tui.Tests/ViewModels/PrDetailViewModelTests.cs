using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrDetailViewModelTests
{
    private static PullRequest Pr() =>
        new(10, "Add feature", "the description", "active", false, "feature", "main", "succeeded",
            "Jin", "repo-1", "web", [new PrReviewer("r1", "Sam", PrVote.NoVote, true)], [101], "abc123",
            "Contoso.Web", ProjectId: "proj-guid-1");

    private sealed class FakeStore : IPullRequestStore
    {
        public PullRequest Pr { get; set; } = PrDetailViewModelTests.Pr();
        public List<PrThread> Threads { get; set; } = [];
        public PrVote? VotedValue { get; private set; }
        public (int thread, string text)? Replied { get; private set; }
        public (int thread, PrThreadStatus status)? StatusSet { get; private set; }
        public bool Abandoned { get; private set; }
        public bool Completed { get; private set; }
        public string? LastProject { get; private set; }
        public string? Commented { get; private set; }
        public string? PolicyProjectId { get; private set; }
        public List<PolicyEvaluation> Policies { get; set; } = [];
        public Exception? PolicyException { get; set; }

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
            Completed = true;
            return Task.CompletedTask;
        }

        public Task AddPrCommentAsync(string project, string repo, int id, string text, CancellationToken ct)
        {
            LastProject = project;
            Commented = text;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string projectId, int id, CancellationToken ct)
        {
            PolicyProjectId = projectId;
            return PolicyException is not null
                ? Task.FromException<IReadOnlyList<PolicyEvaluation>>(PolicyException)
                : Task.FromResult<IReadOnlyList<PolicyEvaluation>>(Policies);
        }
    }

    private static PullRequest PrWithoutSourceCommit() =>
        new(10, "Add feature", "the description", "active", false, "feature", "main", "succeeded",
            "Jin", "repo-1", "web", [new PrReviewer("r1", "Sam", PrVote.NoVote, true)], [101],
            LastMergeSourceCommitId: null, "Contoso.Web");

    [Fact]
    public async Task Complete_Without_Source_Commit_Surfaces_Error_And_Does_Not_Call_Store()
    {
        var store = new FakeStore { Pr = PrWithoutSourceCommit() };
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Merge is still computing (no source commit); completing must surface a clear,
        // user-visible error rather than silently no-op or let an exception escape.
        await vm.CompleteAsync("squash", deleteSource: false, TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("merge", vm.Error);
        Assert.False(store.Completed);
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
    public async Task AddPrComment_Posts_Text()
    {
        var store = new FakeStore();
        var vm = new PrDetailViewModel(store, 10);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.AddPrCommentAsync("looks good overall", TestContext.Current.CancellationToken);

        Assert.Equal("looks good overall", store.Commented);
    }

    [Fact]
    public async Task Load_Populates_Policies()
    {
        var store = new FakeStore
        {
            Policies = [new PolicyEvaluation("Build validation", "approved", true)],
        };
        var vm = new PrDetailViewModel(store, 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(vm.Policies);
        Assert.Equal("Build validation", vm.Policies[0].DisplayName);
    }

    [Fact]
    public async Task Load_Threads_Project_Guid_To_Policy_Fetch()
    {
        var store = new FakeStore();
        var vm = new PrDetailViewModel(store, 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // The artifactId needs the project GUID, not the name.
        Assert.Equal("proj-guid-1", store.PolicyProjectId);
    }

    [Fact]
    public async Task Load_Policy_Failure_Is_Nonfatal_But_Surfaces_Expected_Error()
    {
        var store = new FakeStore { PolicyException = new HttpRequestException("policy down") };
        var vm = new PrDetailViewModel(store, 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // The PR itself still loaded; the expected policy failure surfaces via Error.
        Assert.NotNull(vm.PullRequest);
        Assert.NotNull(vm.Error);
        Assert.Contains("policy down", vm.Error);
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

    private sealed class ThrowingStore(Exception ex) : IPullRequestStore
    {
        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) => Task.FromException<PullRequest>(ex);
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task VoteAsync(string project, string repo, int id, PrVote vote, CancellationToken ct) => Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int id, int threadId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int id, int threadId, PrThreadStatus status, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonAsync(string project, string repo, int id, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string project, string repo, int id, string mergeStrategy, bool deleteSource, CancellationToken ct) => Task.CompletedTask;
        public Task AddPrCommentAsync(string project, string repo, int id, string text, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string project, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PolicyEvaluation>>([]);
    }

    [Fact]
    public async Task Load_Expected_Failure_Surfaces_Error()
    {
        var vm = new PrDetailViewModel(new ThrowingStore(new HttpRequestException("down")), 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("down", vm.Error);
    }

    [Fact]
    public async Task Load_User_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var vm = new PrDetailViewModel(new ThrowingStore(new OperationCanceledException(cts.Token)), 10);

        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Load_Timeout_Cancellation_Surfaces_As_Error()
    {
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var vm = new PrDetailViewModel(new ThrowingStore(new OperationCanceledException(foreign.Token)), 10);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Load_Unexpected_Exception_Propagates()
    {
        var vm = new PrDetailViewModel(new ThrowingStore(new InvalidOperationException("bug")), 10);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
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
