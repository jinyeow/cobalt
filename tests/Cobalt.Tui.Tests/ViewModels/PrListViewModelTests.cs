using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrListViewModelTests
{
    private static PullRequest Pr(int id, string title, string repo = "web") =>
        new(id, title, null, "active", false, "feature", "main", "succeeded", "Jin", "r1", repo, [], [], "abc123");

    private sealed class FakeSource : IPullRequestSource
    {
        public Dictionary<PrListFilter, IReadOnlyList<PullRequest>> ByFilter { get; } = new();
        public List<PrListFilter> Requested { get; } = [];
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct)
        {
            Requested.Add(filter);
            if (Throw is not null)
            {
                return Task.FromException<IReadOnlyList<PullRequest>>(Throw);
            }
            return Task.FromResult(ByFilter.GetValueOrDefault(filter, []));
        }
    }

    [Fact]
    public async Task Starts_On_ReviewQueue_Tab()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.ReviewQueue] = [Pr(1, "review me")];
        var vm = new PrListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrListFilter.ReviewQueue, vm.ActiveTab);
        Assert.Single(vm.Rows);
        Assert.Equal("review me", vm.Rows[0].Title);
    }

    [Fact]
    public async Task Switching_Tab_Loads_That_Filter()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Mine] = [Pr(2, "my pr")];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.SetTabAsync(PrListFilter.Mine, TestContext.Current.CancellationToken);

        Assert.Equal(PrListFilter.Mine, vm.ActiveTab);
        Assert.Contains(PrListFilter.Mine, source.Requested);
        Assert.Equal("my pr", vm.Rows[0].Title);
    }

    [Fact]
    public async Task NextTab_Cycles_Through_Three_Filters()
    {
        var vm = new PrListViewModel(new FakeSource());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Mine, vm.ActiveTab);
        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Active, vm.ActiveTab);
        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.ReviewQueue, vm.ActiveTab);
    }

    [Fact]
    public async Task Error_Is_Surfaced_Not_Thrown()
    {
        var source = new FakeSource { Throw = new InvalidOperationException("boom") };
        var vm = new PrListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("boom", vm.Error);
    }

    private sealed class GatedSource : IPullRequestSource
    {
        private readonly Dictionary<PrListFilter, Queue<TaskCompletionSource<IReadOnlyList<PullRequest>>>> _pending = new();

        public TaskCompletionSource<IReadOnlyList<PullRequest>> Gate(PrListFilter filter)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<PullRequest>>();
            if (!_pending.TryGetValue(filter, out var q))
            {
                q = new Queue<TaskCompletionSource<IReadOnlyList<PullRequest>>>();
                _pending[filter] = q;
            }
            q.Enqueue(tcs);
            return tcs;
        }

        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
            _pending.TryGetValue(filter, out var q) && q.Count > 0
                ? q.Dequeue().Task
                : Task.FromResult<IReadOnlyList<PullRequest>>([]);
    }

    [Fact]
    public async Task Switching_Tab_Blanks_Rows_And_Sets_Loading_Before_Fetch_Completes()
    {
        var source = new GatedSource();
        source.Gate(PrListFilter.ReviewQueue).SetResult([Pr(1, "seed")]);
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.Rows);

        var mine = source.Gate(PrListFilter.Mine);
        var switching = vm.NextTabAsync(TestContext.Current.CancellationToken); // ReviewQueue → Mine

        // Fetch has not completed yet: the pane must already show the loading state
        // with the previous tab's rows cleared.
        Assert.True(vm.IsLoading);
        Assert.Empty(vm.Rows);

        mine.SetResult([Pr(2, "my pr")]);
        await switching;

        Assert.False(vm.IsLoading);
        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].PullRequestId);
    }

    [Fact]
    public async Task Slow_First_Load_Cannot_Clobber_A_Newer_Tab()
    {
        var source = new GatedSource();
        var vm = new PrListViewModel(source);

        var slow = source.Gate(PrListFilter.Mine);
        var loadA = vm.SetTabAsync(PrListFilter.Mine, TestContext.Current.CancellationToken);

        var fast = source.Gate(PrListFilter.Active);
        var loadB = vm.SetTabAsync(PrListFilter.Active, TestContext.Current.CancellationToken);

        // Newer load (B) completes first, then the older, slower load (A) lands last.
        fast.SetResult([Pr(20, "newer")]);
        await loadB;
        slow.SetResult([Pr(10, "older")]);
        await loadA;

        Assert.Equal(PrListFilter.Active, vm.ActiveTab);
        Assert.Single(vm.Rows);
        Assert.Equal(20, vm.Rows[0].PullRequestId);
    }

    [Fact]
    public async Task RepoFilter_Narrows_Active_Rows()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.ReviewQueue] = [Pr(1, "a", "web"), Pr(2, "b", "api")];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.RepositoryFilter = "api";

        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].PullRequestId);
    }
}
