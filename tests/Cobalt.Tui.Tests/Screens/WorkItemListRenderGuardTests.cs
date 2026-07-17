using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// MISSED-A: a render triggered by a loading-state-only Changed (no row/width change) must not
/// reformat every row and rebuild the list source — that work is redundant when nothing the
/// rows are derived from actually changed.
/// </summary>
public class WorkItemListRenderGuardTests
{
    private static readonly IApplication App = Application.Create();

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
        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task Render_Skips_The_Row_Rebuild_When_Rows_And_Width_Are_Unchanged()
    {
        var items = Enumerable.Range(1, 5).Select(i => Wi(i)).ToList();
        var vm = new WorkItemListViewModel(new FakeWiSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new WorkItemListView(App, vm);
        var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 20));

        var before = view.ListSource;
        view.Render(); // nothing changed since the layout-triggered render above

        Assert.Same(before, view.ListSource);
    }

    [Fact]
    public async Task Render_Rebuilds_When_The_Width_Genuinely_Changes()
    {
        // Resize goes through OnViewportChanged -> Render() directly (no vm.Changed involved,
        // which would need app.Invoke and a real Init()), so it's the one live-wired way to
        // prove the guard doesn't over-suppress: a width change must still force a rebuild
        // (rows are padded to the list's current width).
        var items = Enumerable.Range(1, 5).Select(i => Wi(i)).ToList();
        var vm = new WorkItemListViewModel(new FakeWiSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new WorkItemListView(App, vm);
        var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 20));

        var before = view.ListSource;
        window.Layout(new System.Drawing.Size(40, 20));

        Assert.NotSame(before, view.ListSource);
    }

    [Fact]
    public async Task Render_Rebuilds_When_A_Filter_Change_Narrows_The_Rows()
    {
        // Pins the contract the guard relies on: ApplyFilter reassigns Rows to a NEW reference
        // (so ReferenceEquals(_vm.Rows, _renderedRows) is false and the guard rebuilds) even
        // though the width is unchanged. If ApplyFilter ever kept the same reference, the guard
        // would wrongly skip the rebuild and the filtered rows would never paint.
        var items = Enumerable.Range(1, 5).Select(i => Wi(i)).ToList();
        var vm = new WorkItemListViewModel(new FakeWiSource(items));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new WorkItemListView(App, vm);
        var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 20));

        var before = view.ListSource;
        // The Filter setter reassigns Rows, then raises Changed → OnVmChanged → app.Invoke,
        // which throws without Application.Init() (this suite never Inits — the same pattern
        // as the other view-level tests). Rows is already reassigned by then, so swallow the
        // marshalling throw and drive Render() directly to observe the guard.
        try
        {
            vm.Filter = "item 1";
        }
        catch (Terminal.Gui.App.NotInitializedException)
        {
        }
        view.Render();

        Assert.NotSame(before, view.ListSource);
    }
}
