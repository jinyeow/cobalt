using System.Text.Json;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class WorkItemListViewModelTests
{
    private static WorkItem Item(long id, string title, string state, string type = "Bug") =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = JsonEl($"\"{title}\""),
            ["System.State"] = JsonEl($"\"{state}\""),
            ["System.WorkItemType"] = JsonEl($"\"{type}\""),
        });

    private static JsonElement JsonEl(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSource(IReadOnlyList<WorkItem> items) : IWorkItemSource
    {
        public Exception? Throw { get; init; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(CancellationToken ct)
        {
            Calls++;
            return Throw is not null ? Task.FromException<IReadOnlyList<WorkItem>>(Throw) : Task.FromResult(items);
        }
    }

    [Fact]
    public async Task Load_Populates_Rows_And_Clears_Loading()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active"), Item(2, "B", "New")]));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("A", vm.Rows[0].Title);
    }

    [Fact]
    public async Task Load_Expected_Failure_Sets_Error_And_Clears_Rows()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active")])
        {
            Throw = new HttpRequestException("boom"),
        });

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.Error);
        Assert.Contains("boom", vm.Error);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Load_Cancellation_Propagates()
    {
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new OperationCanceledException() });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_Unexpected_Exception_Propagates()
    {
        // An unexpected type is a bug, not an API error — it must escape to the global
        // crash boundary rather than being swallowed into the Error surface.
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new InvalidOperationException("bug") });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Filter_Narrows_Visible_Rows_By_Title_And_Id()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "Fix login", "Active"), Item(2, "Add logs", "New")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "login";
        Assert.Single(vm.Rows);
        Assert.Equal("Fix login", vm.Rows[0].Title);

        vm.Filter = "2";
        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].Id);

        vm.Filter = "";
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Filter_Is_Case_Insensitive()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "Fix Login", "Active")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "LOGIN";

        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Selected_Item_Tracks_Index()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active"), Item(2, "B", "New")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.SelectedIndex = 1;

        Assert.Equal(2, vm.Selected?.Id);
    }

    [Fact]
    public async Task Selected_Is_Null_When_Empty()
    {
        var vm = new WorkItemListViewModel(new FakeSource([]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(vm.Selected);
    }
}
