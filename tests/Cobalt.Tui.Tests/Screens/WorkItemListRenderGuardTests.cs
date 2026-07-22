using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// MISSED-A: a render triggered by a loading-state-only Changed (no row/width change) must not
/// reformat every row and rebuild the list source — that work is redundant when nothing the
/// rows are derived from actually changed.
/// </summary>
public class WorkItemListRenderGuardTests
{
    // Undrained by design: mirrors today's headless "Invoke never drains" so the views' posted
    // renders never fire on their own — tests call view.Render() explicitly (M2).
    private static readonly RecordingUiPost App = new();

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
        using var window = new Window();
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
        using var window = new Window();
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
        using var window = new Window();
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

    [Fact]
    public async Task Render_Shows_The_Vm_EmptyStateText_As_The_Placeholder_Row()
    {
        var vm = new WorkItemListViewModel(new FakeWiSource([])); // no items — genuinely empty
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new WorkItemListView(App, vm);
        using var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 20));

        Assert.Equal(vm.EmptyStateText, view.RowText(0));
    }

    private sealed class GatedWiSource : IWorkItemSource
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<WorkItem>>> _gates = new();

        public TaskCompletionSource<IReadOnlyList<WorkItem>> NextGate()
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<WorkItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(tcs);
            return tcs;
        }

        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
            _gates.Dequeue().Task;
    }

    [Fact]
    public async Task Render_Clears_The_Stale_Placeholder_When_A_Reload_Starts()
    {
        // WorkItemListViewModel.LoadAsync raises Changed at the START of a reload without
        // reassigning Rows (only ApplyFilter at the END does), so a render guard keyed only on
        // Rows-reference/width would leave a previous placeholder painted through the reload.
        var source = new GatedWiSource();
        var vm = new WorkItemListViewModel(source);
        var firstGate = source.NextGate();
        var firstLoad = vm.LoadAsync(TestContext.Current.CancellationToken);
        firstGate.SetResult([]);
        await firstLoad;

        var view = new WorkItemListView(App, vm);
        using var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(60, 20));

        var placeholder = vm.EmptyStateText;
        Assert.Equal(placeholder, view.RowText(0));

        source.NextGate(); // the reload triggered below is left in flight, never completes
        try
        {
            // Setter reassigns _includeCompleted then fires a background reload synchronously up
            // to its first await; the Changed it raises tries app.Invoke, which throws without
            // Application.Init() (same pattern as the other tests in this suite).
            vm.IncludeCompleted = true;
        }
        catch (Terminal.Gui.App.NotInitializedException)
        {
        }
        view.Render();

        Assert.True(vm.IsLoading);
        Assert.NotEqual(placeholder, view.RowText(0));
    }
}
