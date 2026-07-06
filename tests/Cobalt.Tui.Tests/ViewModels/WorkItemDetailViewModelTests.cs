using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class WorkItemDetailViewModelTests
{
    private static WorkItem Item(long id, string title, string state, string desc = "", string type = "Bug") =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El($"\"{title}\""),
            ["System.State"] = El($"\"{state}\""),
            ["System.WorkItemType"] = El($"\"{type}\""),
            ["System.Description"] = El($"\"{desc}\""),
        });

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeStore : IWorkItemStore
    {
        public WorkItem Item { get; set; } = Item(1, "T", "New");
        public IReadOnlyList<WorkItemComment> Comments { get; set; } = [];
        public IReadOnlyList<WorkItemStateDto> States { get; set; } =
            [new() { Name = "New" }, new() { Name = "Active" }, new() { Name = "Resolved" }];
        public JsonPatchBuilder? LastPatch { get; private set; }
        public string? LastComment { get; private set; }

        public Task<WorkItem> GetWorkItemAsync(long id, CancellationToken ct) => Task.FromResult(Item);
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, CancellationToken ct) => Task.FromResult(Comments);
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, CancellationToken ct) => Task.FromResult(States);

        public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, CancellationToken ct)
        {
            LastPatch = patch;
            return Task.FromResult(Item);
        }

        public Task<WorkItemComment> AddCommentAsync(long id, string text, CancellationToken ct)
        {
            LastComment = text;
            return Task.FromResult(new WorkItemComment(9, "me", DateTimeOffset.UnixEpoch, text));
        }
    }

    [Fact]
    public async Task Load_Fetches_Item_States_And_Comments()
    {
        var store = new FakeStore
        {
            Item = Item(1, "Fix", "New", "<p>hello</p>"),
            Comments = [new(1, "Jin", DateTimeOffset.UnixEpoch, "hi")],
        };
        var vm = new WorkItemDetailViewModel(store, 1);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Fix", vm.Item?.Title);
        Assert.Contains("hello", vm.DescriptionMarkdown);
        Assert.Single(vm.Comments);
        Assert.Equal(["New", "Active", "Resolved"], vm.AvailableStates.Select(s => s.Name));
    }

    [Fact]
    public async Task ChangeState_Sends_Patch_With_State_Field()
    {
        var store = new FakeStore { Item = Item(1, "Fix", "New") };
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ChangeStateAsync("Active", TestContext.Current.CancellationToken);

        Assert.NotNull(store.LastPatch);
        Assert.Contains("/fields/System.State", store.LastPatch!.ToJson());
        Assert.Contains("Active", store.LastPatch.ToJson());
    }

    [Fact]
    public async Task AddComment_Passes_Text_And_Refreshes()
    {
        var store = new FakeStore();
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.AddCommentAsync("looks good", TestContext.Current.CancellationToken);

        Assert.Equal("looks good", store.LastComment);
    }

    [Fact]
    public async Task SaveDescription_Converts_Markdown_To_Html_Patch()
    {
        var store = new FakeStore { Item = Item(1, "Fix", "New") };
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.SaveDescriptionAsync("Hello **world**", TestContext.Current.CancellationToken);

        var json = store.LastPatch!.ToJson();
        Assert.Contains("/fields/System.Description", json);
        Assert.Contains("<strong>world</strong>", json);
    }

    [Fact]
    public async Task DescriptionLossy_True_For_Rich_Html()
    {
        var store = new FakeStore { Item = Item(1, "Fix", "New", "<table><tr><td>x</td></tr></table>") };
        var vm = new WorkItemDetailViewModel(store, 1);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.DescriptionLossy);
    }

    [Fact]
    public async Task Update_Expected_Failure_Surfaces_Error_And_Does_Not_Throw()
    {
        var store = new ThrowingStore(new HttpRequestException("nope"));
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ChangeStateAsync("Active", TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Load_Expected_Failure_Surfaces_Error()
    {
        var store = new ThrowingStore(new AdoApiException(System.Net.HttpStatusCode.BadGateway, "down"), onLoad: true);
        var vm = new WorkItemDetailViewModel(store, 1);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("down", vm.Error);
    }

    [Fact]
    public async Task Update_Unexpected_Exception_Propagates()
    {
        var store = new ThrowingStore(new InvalidOperationException("bug"));
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.ChangeStateAsync("Active", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_User_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new ThrowingStore(new OperationCanceledException(cts.Token));
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.ChangeStateAsync("Active", cts.Token));
    }

    [Fact]
    public async Task Update_Timeout_Cancellation_Surfaces_As_Error()
    {
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var store = new ThrowingStore(new OperationCanceledException(foreign.Token));
        var vm = new WorkItemDetailViewModel(store, 1);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.ChangeStateAsync("Active", TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }

    private sealed class ThrowingStore(Exception ex, bool onLoad = false) : IWorkItemStore
    {
        public Task<WorkItem> GetWorkItemAsync(long id, CancellationToken ct) =>
            onLoad ? Task.FromException<WorkItem>(ex) : Task.FromResult(Item(1, "T", "New"));
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItemComment>>([]);
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItemStateDto>>([]);
        public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, CancellationToken ct) =>
            Task.FromException<WorkItem>(ex);
        public Task<WorkItemComment> AddCommentAsync(long id, string text, CancellationToken ct) =>
            Task.FromException<WorkItemComment>(ex);
    }
}
