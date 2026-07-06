using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// Unit-tests the shared work-item action runners (the list entry points) with a
/// fake store/editor and injected chooser + inline UI-post, so the flows run
/// headlessly. Guards that each verb reaches the view-model with the right payload.
/// </summary>
public class WorkItemActionsTests
{
    private static readonly IApplication App = Application.Create();

    private static WorkItem Item(long id = 1, string type = "Bug", string tags = "") =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El("\"T\""),
            ["System.State"] = El("\"New\""),
            ["System.WorkItemType"] = El($"\"{type}\""),
            ["System.Tags"] = El($"\"{tags}\""),
        });

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeStore : IWorkItemStore
    {
        public WorkItem Item { get; set; } = WorkItemActionsTests.Item();
        public IReadOnlyList<WorkItemStateDto> States { get; set; } =
            [new() { Name = "New" }, new() { Name = "Active" }, new() { Name = "Resolved" }];
        public JsonPatchBuilder? LastPatch { get; private set; }
        public string? LastComment { get; private set; }

        public Task<WorkItem> GetWorkItemAsync(long id, CancellationToken ct) => Task.FromResult(Item);
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItemComment>>([]);
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, CancellationToken ct) =>
            Task.FromResult(States);
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

    private static EditorService EditorReturning(string? text)
    {
        var launcher = new FakeLauncher(text);
        return new EditorService(launcher);
    }

    private sealed class FakeLauncher(string? textToWrite) : IEditorLauncher
    {
        public Task<int> LaunchAsync(string filePath, CancellationToken ct)
        {
            if (textToWrite is not null)
            {
                File.WriteAllText(filePath, textToWrite);
            }
            return Task.FromResult(0);
        }
    }

    private static WorkItemActions Actions(EditorService editor, Action<string> log, int? choice = null) =>
        new(App, editor, log, choose: (_, _) => choice, post: a => a());

    [Fact]
    public async Task RunComment_Posts_The_Edited_Text()
    {
        var store = new FakeStore();
        var actions = Actions(EditorReturning("looks good"), _ => { });

        await actions.RunCommentAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.Equal("looks good", store.LastComment);
    }

    [Fact]
    public async Task RunComment_Empty_Editor_Posts_Nothing()
    {
        var store = new FakeStore();
        var actions = Actions(EditorReturning(null), _ => { });

        await actions.RunCommentAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.Null(store.LastComment);
    }

    [Fact]
    public async Task RunChangeState_Sends_Chosen_State_Patch()
    {
        var store = new FakeStore();
        // index 1 → "Active"
        var actions = Actions(EditorReturning(null), _ => { }, choice: 1);

        await actions.RunChangeStateAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.NotNull(store.LastPatch);
        Assert.Contains("/fields/System.State", store.LastPatch!.ToJson());
        Assert.Contains("Active", store.LastPatch.ToJson());
    }

    [Fact]
    public async Task RunChangeState_Dismissed_Chooser_Sends_No_Patch()
    {
        var store = new FakeStore();
        var actions = Actions(EditorReturning(null), _ => { }, choice: null);

        await actions.RunChangeStateAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.Null(store.LastPatch);
    }

    [Fact]
    public async Task RunAssign_Sends_AssignedTo_Patch()
    {
        var store = new FakeStore();
        var actions = Actions(EditorReturning("jin@example.com"), _ => { });

        await actions.RunAssignAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.NotNull(store.LastPatch);
        Assert.Contains("/fields/System.AssignedTo", store.LastPatch!.ToJson());
        Assert.Contains("jin@example.com", store.LastPatch.ToJson());
    }

    [Fact]
    public async Task RunTags_Sends_Tags_Patch()
    {
        var store = new FakeStore();
        var actions = Actions(EditorReturning("bug; ui"), _ => { });

        await actions.RunTagsAsync(store, 1, TestContext.Current.CancellationToken);

        Assert.NotNull(store.LastPatch);
        Assert.Contains("/fields/System.Tags", store.LastPatch!.ToJson());
        Assert.Contains("bug; ui", store.LastPatch.ToJson());
    }
}
