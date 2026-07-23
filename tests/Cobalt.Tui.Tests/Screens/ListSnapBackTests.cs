using System.Collections.ObjectModel;
using System.Text.Json;
using Cobalt.Core.Models;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: guards vim navigation routing through the real
/// tokenizer → router → VimScroll → ListView chain, and the selection
/// snap-back fix in the list views' Render (a background reload must not reset
/// the highlight to the top).
/// </summary>
public class ListSnapBackTests
{
    // Undrained by design: mirrors today's headless "Invoke never drains" so the views' posted
    // renders never fire on their own — tests call view.Render() explicitly (M2).
    private static readonly RecordingUiPost App = new();

    // ---- (1) navigation routing regression ----

    private static void Route(KeymapRouter router, ListView list, KeyScope scope, Key key)
    {
        var token = KeyTokenizer.ToToken(key);
        if (token is null)
        {
            return;
        }
        var result = router.Feed(token, scope);
        if (result.Kind == KeyResultKind.Matched && VimScroll.Applies(result.Command))
        {
            VimScroll.Apply(list, result.Command, result.Count);
        }
    }

    [Fact]
    public void Vim_Tokens_J_J_G_G_G_Land_At_Top()
    {
        var router = new KeymapRouter(KeyBindingTable.Default());
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        list.SetSource(new ObservableCollection<string>(Enumerable.Range(0, 10).Select(i => $"row {i}")));
        var window = new Window();
        window.Add(list);
        window.Layout(new System.Drawing.Size(40, 12));
        list.SetFocus();

        Route(router, list, KeyScope.WorkItemList, new Key('j'));
        Route(router, list, KeyScope.WorkItemList, new Key('j'));
        Route(router, list, KeyScope.WorkItemList, new Key('G'));
        Route(router, list, KeyScope.WorkItemList, new Key('g'));
        Route(router, list, KeyScope.WorkItemList, new Key('g'));

        Assert.Equal(0, list.SelectedItem);
    }

    // ---- (2) snap-back on reload ----

    private static WorkItem Wi(long id) =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El($"\"item {id}\""),
            ["System.State"] = El("\"Active\""),
            ["System.WorkItemType"] = El("\"Bug\""),
        });

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeWiSource(IReadOnlyList<WorkItem> items) : IWorkItemSource
    {
        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(Cobalt.Core.Ado.WorkItemQuery query, CancellationToken ct) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task WorkItemList_Reload_Preserves_Navigated_Selection()
    {
        var items = Enumerable.Range(1, 8).Select(i => Wi(i)).ToList();
        var vm = new WorkItemListViewModel(new FakeWiSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Build the view after the load so no Changed fires through app.Invoke.
        var view = new WorkItemListView(App, vm);
        view.Navigate(AppCommand.MoveBottom); // reviewer moves to the last row

        // A background reload (refresh / returning from detail) re-renders the list.
        view.Render();

        // Selection must stay on the last row, not snap back to the top.
        Assert.Equal(items[^1].Id, view.SelectedId);
    }

    private static PullRequest Pr(int id) =>
        new(id, $"pr {id}", null, "active", false, "feature", "main", "succeeded", "Jin", "r1", "web", [], [], "abc");

    private sealed class FakePrSource(IReadOnlyList<PullRequest> items) : IPullRequestSource
    {
        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task PrList_Reload_Preserves_Navigated_Selection()
    {
        var items = Enumerable.Range(1, 8).Select(Pr).ToList();
        var vm = new PrListViewModel(new FakePrSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new PrListView(App, vm);
        view.Navigate(AppCommand.MoveBottom);

        view.Render();

        Assert.Equal(items[^1].PullRequestId, view.SelectedPr?.PullRequestId);
    }

    // ---- MISSED-A: skip the row re-format when rows/width/counts are unchanged ----

    [Fact]
    public async Task PrList_Render_Skips_Reformat_When_Nothing_Changed()
    {
        var items = Enumerable.Range(1, 5).Select(Pr).ToList();
        var vm = new PrListViewModel(new FakePrSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var view = new PrListView(App, vm);

        view.Render();
        var formatted = view.RenderedRows;
        view.Render(); // identical rows, width, and (no enricher) counts

        // A redundant render must not re-format every row: the formatted-row list is reused, not
        // rebuilt, so a burst of no-op renders is not O(rows) each.
        Assert.Same(formatted, view.RenderedRows);
    }

    // ---- CACHE-2: enrich only the visible slice (+margin), not every loaded row ----

    private static async Task WaitForStableAsync(Func<int> count)
    {
        var last = -1;
        for (var i = 0; i < 100; i++)
        {
            var now = count();
            if (now == last && now > 0)
            {
                return;
            }
            last = now;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task PrList_Enriches_Only_The_Visible_Slice_Not_Every_Row()
    {
        var items = Enumerable.Range(1, 100).Select(Pr).ToList();
        var vm = new PrListViewModel(new FakePrSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var fetched = new List<int>();
        var enricher = new PrCommentCountEnricher((pr, _) =>
        {
            lock (fetched) { fetched.Add(pr.PullRequestId); }
            return Task.FromResult(0);
        });

        var view = new PrListView(App, vm, enricher);
        var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 12)); // small viewport → most rows off-screen
        view.Render();

        await WaitForStableAsync(() => { lock (fetched) { return fetched.Count; } });

        int total;
        bool hasTop, hasBottom;
        lock (fetched)
        {
            total = fetched.Count;
            hasTop = fetched.Contains(1);
            hasBottom = fetched.Contains(100);
        }

        // Only the top of the list is on screen: enrich those (+ a small margin), never all 100.
        Assert.True(total < 100, $"expected a viewport slice, enriched {total}/100");
        Assert.True(hasTop, "the visible top rows must be enriched");
        Assert.False(hasBottom, "far-off-screen rows must not be enriched until scrolled to");

        view.Dispose();
    }

    // ---- (3) enrichment cancellation on tab switch (M2 / ADR 0012) ----

    [Fact]
    public async Task PrList_TabSwitch_Cancels_Prior_Tab_Enrichment()
    {
        var started = new TaskCompletionSource();
        var tokens = new List<CancellationToken>();
        var enricher = new PrCommentCountEnricher(async (pr, ct) =>
        {
            lock (tokens)
            {
                tokens.Add(ct);
            }
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return 0;
        });

        var items = Enumerable.Range(1, 3).Select(Pr).ToList();
        var vm = new PrListViewModel(new FakePrSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new PrListView(App, vm, enricher); // ctor render enqueues enrichment
        await started.Task; // a fetch has captured the current load token

        CancellationToken priorToken;
        lock (tokens)
        {
            priorToken = tokens[0];
        }
        Assert.False(priorToken.IsCancellationRequested);

        view.NextTab(); // cycles the per-load token, cancelling the prior tab's enrichment

        Assert.True(priorToken.IsCancellationRequested);        // prior fetch observes cancellation
        Assert.False(view.CurrentLoadToken.IsCancellationRequested); // new tab's load token is live

        view.Dispose(); // cancel the lifetime token so no infinite-delay fetch is left pending
    }
}
