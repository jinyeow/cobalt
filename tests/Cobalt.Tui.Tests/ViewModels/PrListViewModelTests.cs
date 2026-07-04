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
