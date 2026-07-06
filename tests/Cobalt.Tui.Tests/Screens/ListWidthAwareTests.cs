using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: guards the width-aware re-render wiring (ViewportChanged →
/// Render at the new width) and that a landed comment count decorates its row.
/// </summary>
public class ListWidthAwareTests
{
    private static readonly IApplication App = Application.Create();

    private static PullRequest Pr(int id, string title = "a pull request title", string repo = "web") =>
        new(id, title, null, "active", false, "feature", "main", "succeeded", "Jin", "r1", repo,
            [], [], "abc", "Core", DateTimeOffset.UtcNow.AddDays(-2));

    private sealed class FakePrSource(IReadOnlyList<PullRequest> items) : IPullRequestSource
    {
        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task PrList_Reformats_Rows_On_Resize()
    {
        var vm = new PrListViewModel(new FakePrSource([Pr(1), Pr(2), Pr(3)]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new PrListView(App, vm);
        var window = new Window();
        window.Add(view);

        window.Layout(new System.Drawing.Size(140, 30));
        var wide = view.RowText(0);

        window.Layout(new System.Drawing.Size(80, 30));
        var narrow = view.RowText(0);

        // Each render pads rows to the list's current width, so a resize must reformat.
        Assert.Equal(view.ListWidth, narrow.Length);
        Assert.NotEqual(wide.Length, narrow.Length);
    }

    [Fact]
    public async Task PrList_Shows_Comment_Badge_When_Count_Lands()
    {
        var fetched = new List<int>();
        var enricher = new PrCommentCountEnricher((pr, _) =>
        {
            fetched.Add(pr.PullRequestId);
            return Task.FromResult(4);
        });

        var vm = new PrListViewModel(new FakePrSource([Pr(1)]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new PrListView(App, vm, enricher);
        var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(120, 30));

        // The view enriches loaded rows; the fake fetcher completes synchronously,
        // so the cache is warm. A render then decorates the row with the badge.
        Assert.Contains(1, fetched);
        view.Render();
        Assert.Contains("💬 4", view.RowText(0));
    }
}
