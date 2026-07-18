using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrListViewModelTests
{
    private static PullRequest Pr(int id, string title, string repo = "web", string project = "Fabrikam") =>
        new(id, title, null, "active", false, "feature", "main", "succeeded", "Jin", "r1", repo, [], [], "abc123", project);

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
    public async Task Starts_On_Team_Tab()
    {
        // Team is the first (default) tab: review-via-team is the common ADO setup,
        // so the personal review queue is no longer in the cycle (always empty there).
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Team] = [Pr(1, "review me")];
        var vm = new PrListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrListFilter.Team, vm.ActiveTab);
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
    public async Task NextTab_Cycles_Team_Mine_Active()
    {
        var vm = new PrListViewModel(new FakeSource());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Tab order: Team → Mine → Active → (wrap) Team.
        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Mine, vm.ActiveTab);
        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Active, vm.ActiveTab);
        await vm.NextTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Team, vm.ActiveTab);
    }

    [Fact]
    public async Task PrevTab_From_Team_Wraps_To_Active()
    {
        var vm = new PrListViewModel(new FakeSource());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.PrevTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Active, vm.ActiveTab);
        await vm.PrevTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Mine, vm.ActiveTab);
        await vm.PrevTabAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PrListFilter.Team, vm.ActiveTab);
    }

    [Fact]
    public async Task Expected_Error_Is_Surfaced_Not_Thrown()
    {
        var source = new FakeSource { Throw = new HttpRequestException("boom") };
        var vm = new PrListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("boom", vm.Error);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task User_Cancellation_Propagates()
    {
        // A genuine user/dialog cancel throws an OCE carrying the caller's own token.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = new FakeSource { Throw = new OperationCanceledException(cts.Token) };
        var vm = new PrListViewModel(source);

        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Timeout_Cancellation_Surfaces_As_Error()
    {
        // An HttpClient timeout surfaces as an OCE whose token is NOT the caller's; it must
        // become a visible error, not a silent no-data pane (L2).
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var source = new FakeSource { Throw = new OperationCanceledException(foreign.Token) };
        var vm = new PrListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken); // caller token is NOT cancelled

        Assert.NotNull(vm.Error);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Unexpected_Exception_Propagates()
    {
        var source = new FakeSource { Throw = new InvalidOperationException("bug") };
        var vm = new PrListViewModel(source);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
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
        source.Gate(PrListFilter.Team).SetResult([Pr(1, "seed")]);
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.Rows);

        var mine = source.Gate(PrListFilter.Mine);
        var switching = vm.NextTabAsync(TestContext.Current.CancellationToken); // Team → Mine

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
    public async Task Revisiting_A_Loaded_Tab_Paints_Cached_Rows_Before_The_Refresh()
    {
        var source = new GatedSource();
        source.Gate(PrListFilter.Team).SetResult([Pr(1, "seed")]);
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken); // Team → [1], cached
        source.Gate(PrListFilter.Mine).SetResult([Pr(2, "mine")]);
        await vm.SetTabAsync(PrListFilter.Mine, TestContext.Current.CancellationToken); // Mine → [2], cached

        // Switch back to Team, but hold the refresh so we can see what paints first.
        var refresh = source.Gate(PrListFilter.Team);
        var switching = vm.SetTabAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        // The cached rows paint immediately (no blank pane), while the tab shows as refreshing.
        Assert.True(vm.IsLoading);
        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.Rows[0].PullRequestId);

        refresh.SetResult([Pr(1, "seed"), Pr(3, "fresh")]);
        await switching;

        Assert.False(vm.IsLoading);
        Assert.Equal([1, 3], vm.Rows.Select(r => r.PullRequestId));
    }

    [Fact]
    public async Task InvalidateCache_Stops_A_Revisit_From_Painting_Stale_Rows()
    {
        var source = new GatedSource();
        source.Gate(PrListFilter.Team).SetResult([Pr(1, "seed")]);
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        source.Gate(PrListFilter.Mine).SetResult([Pr(2, "mine")]);
        await vm.SetTabAsync(PrListFilter.Mine, TestContext.Current.CancellationToken);

        // A scope/context change invalidates every tab's cached rows.
        vm.InvalidateCache();

        var refresh = source.Gate(PrListFilter.Team);
        var switching = vm.SetTabAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        // No stale paint: the cache was dropped, so the pane blanks and waits for the fresh fetch.
        Assert.Empty(vm.Rows);

        refresh.SetResult([Pr(1, "seed")]);
        await switching;
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task InvalidateActiveTab_Drops_The_Active_Tab_So_A_Refresh_Does_Not_Paint_Stale()
    {
        var source = new GatedSource();
        source.Gate(PrListFilter.Team).SetResult([Pr(1, "seed")]);
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken); // Team [1] cached, active = Team
        Assert.Single(vm.Rows);

        // A mutation (vote/abandon) drops the active tab's cache so a transient refresh failure
        // shows a blank pane rather than a stale row that no longer reflects the change.
        vm.InvalidateActiveTab();

        var refresh = source.Gate(PrListFilter.Team);
        var reload = vm.LoadAsync(TestContext.Current.CancellationToken);

        // No stale paint: the active tab's cache was dropped, so the pane blanks and waits.
        Assert.Empty(vm.Rows);

        refresh.SetResult([Pr(1, "seed")]);
        await reload;
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task RepoFilter_Narrows_Active_Rows()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Team] = [Pr(1, "a", "web"), Pr(2, "b", "api")];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.RepositoryFilter = "api";

        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].PullRequestId);
    }

    [Fact]
    public async Task ProjectFilter_Narrows_Rows_By_Project_Name()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Team] =
            [Pr(1, "a", "web", "Fabrikam"), Pr(2, "b", "api", "Contoso")];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ProjectFilter = "Contoso";

        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].PullRequestId);
    }

    [Fact]
    public async Task ProjectFilter_And_RepoFilter_Compose()
    {
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Team] =
        [
            Pr(1, "a", "web", "Fabrikam"),
            Pr(2, "b", "api", "Fabrikam"),
            Pr(3, "c", "api", "Contoso"),
        ];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ProjectFilter = "Fabrikam";
        vm.RepositoryFilter = "api";

        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].PullRequestId);
    }

    [Fact]
    public async Task ProjectFilter_Is_Exact_Not_Substring()
    {
        // `:project Web` must exclude a "WebApps" PR (exact, case-insensitive), matching the
        // work-item side's WIQL equality (M4).
        var source = new FakeSource();
        source.ByFilter[PrListFilter.Team] =
            [Pr(1, "a", "web", "Web"), Pr(2, "b", "api", "WebApps")];
        var vm = new PrListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ProjectFilter = "Web";

        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.Rows[0].PullRequestId);
    }
}
