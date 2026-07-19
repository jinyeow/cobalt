using System.Drawing;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

#pragma warning disable CS0618 // asserting on the read-only TextView pane the dialogs build

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: builds each detail dialog and drives keys through the real
/// Terminal.Gui routing (deepest focused child first). Guards that verb keys reach the
/// handler even though a focused ReadOnly TextView would otherwise swallow them, and
/// that non-verb keys still fall through to the TextView's native scrolling.
/// </summary>
public class DetailDialogKeyDeliveryTests
{
    private static readonly IApplication App = Application.Create();

    private static EditorService NoopEditor() =>
        new(new FakeLauncher());

    private sealed class FakeLauncher : IEditorLauncher
    {
        public Task<int> LaunchAsync(string path, CancellationToken ct) => Task.FromResult(0);
    }

    /// <summary>
    /// Fake ITextInput for PrDetailDialog's migrated flows (ADR 0020). Records every request and
    /// returns queued values in order (e.g. the thread-id prompt, then the reply text) — falls
    /// back to the last queued value (or null) once the queue is exhausted.
    /// </summary>
    private sealed class FakeTextInput(params string?[] textsToReturn) : ITextInput
    {
        private int _next;

        public List<TextInputRequest> Requests { get; } = [];

        public Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var value = textsToReturn.Length == 0 ? null
                : textsToReturn[Math.Min(_next, textsToReturn.Length - 1)];
            _next++;
            return Task.FromResult(value);
        }
    }

    private static ITextInput NoopTextInput() => new FakeTextInput();

    // ---- PR detail ----

    private sealed class FakePrStore : IPullRequestStore
    {
        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task VoteAsync(string project, string repositoryId, int id, PrVote vote, CancellationToken ct) => Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repositoryId, int id, int threadId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonAsync(string project, string repositoryId, int id, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string project, string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct) => Task.CompletedTask;
        public Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string project, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PolicyEvaluation>>([]);
    }

    private static PrDetailDialog NewPrDialog(ITextInput? textInput = null)
    {
        var vm = new PrDetailViewModel(new FakePrStore(), 42);
        return new PrDetailDialog(App, vm, textInput ?? NoopTextInput(), _ => { });
    }

    [Fact]
    public void PrDialog_Focuses_The_TextView_Body()
    {
        var detail = NewPrDialog();
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        Assert.IsType<TextView>(dialog.Focused);
    }

    [Fact]
    public void PrDialog_Q_Closes_Even_When_TextView_Has_Focus()
    {
        var detail = NewPrDialog();
        var closed = false;
        detail.CloseAction = () => closed = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('q'));

        Assert.True(closed);
    }

    [Fact]
    public void PrDialog_D_Reaches_The_Diff_Open_Path()
    {
        var detail = NewPrDialog();
        var opened = false;
        detail.DiffAction = () => opened = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('d'));

        Assert.True(opened);
    }

    [Fact]
    public void PrDialog_C_Reaches_The_Reply_Seam()
    {
        var detail = NewPrDialog();
        var replied = false;
        detail.ReplyAction = () => replied = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.True(replied);
    }

    [Fact]
    public void PrDialog_Gc_Reaches_The_Add_Comment_Seam()
    {
        var detail = NewPrDialog();
        var commented = false;
        detail.AddCommentAction = () => commented = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('c'));

        Assert.True(commented);
    }

    [Fact]
    public void PrDialog_PageDown_Still_Scrolls_The_Body()
    {
        var detail = NewPrDialog();
        var dialog = detail.Build();
        dialog.Layout(new Size(60, 10));
        detail.Body.Text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        dialog.SetFocus();

        var before = detail.Body.Viewport.Y;
        dialog.NewKeyDownEvent(Key.PageDown);

        Assert.True(detail.Body.Viewport.Y > before,
            $"expected scroll to advance from {before}, got {detail.Body.Viewport.Y}");
    }

    // Vim scrolling asserts on TextView.CurrentRow — Viewport.Y clamps at Height-1 on
    // TextView and is not a reliable scroll offset (TG 2.4.16, ADR).

    private static PrDetailDialog LaidOutPrDialog(out Terminal.Gui.Views.Dialog dialog)
    {
        var detail = NewPrDialog();
        dialog = detail.Build();
        dialog.Layout(new Size(60, 12));
        detail.Body.Text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        dialog.SetFocus();
        return detail;
    }

    [Fact]
    public void PrDialog_J_Moves_Body_Down_One_Row()
    {
        var detail = LaidOutPrDialog(out var dialog);

        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(1, detail.Body.CurrentRow);
    }

    [Fact]
    public void PrDialog_Count_Then_J_Moves_By_Count()
    {
        var detail = LaidOutPrDialog(out var dialog);

        dialog.NewKeyDownEvent(new Key('5'));
        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(5, detail.Body.CurrentRow);
    }

    [Fact]
    public void PrDialog_G_Scrolls_Last_Page_And_gg_Back_To_Top()
    {
        var detail = LaidOutPrDialog(out var dialog);
        var maxTop = detail.Body.Lines - detail.Body.Viewport.Height; // pager: top of the last page

        dialog.NewKeyDownEvent(new Key('G'));
        Assert.Equal(maxTop, detail.Body.CurrentRow);

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('g'));
        Assert.Equal(0, detail.Body.CurrentRow);
    }

    [Fact]
    public void PrDialog_CtrlD_Moves_About_Half_A_Page()
    {
        var detail = LaidOutPrDialog(out var dialog);
        var half = Math.Max(1, detail.Body.Viewport.Height / 2);

        dialog.NewKeyDownEvent(new Key('d').WithCtrl);

        Assert.Equal(half, detail.Body.CurrentRow);
    }

    [Fact]
    public void PrDialog_Question_Mark_Opens_The_Help_Seam()
    {
        var detail = NewPrDialog();
        var opened = false;
        detail.HelpAction = () => opened = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('?'));

        Assert.True(opened);
    }

    [Fact]
    public void PrDialog_C_Complete_And_A_Abandon_Reach_Their_Seams()
    {
        var detail = NewPrDialog();
        var completed = false;
        var abandoned = false;
        detail.CompleteAction = () => completed = true;
        detail.AbandonAction = () => abandoned = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('C'));
        dialog.NewKeyDownEvent(new Key('A'));

        Assert.True(completed);
        Assert.True(abandoned);
    }

    private sealed class PolicyRenderStore : IPullRequestStore
    {
        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) =>
            Task.FromResult(new PullRequest(42, "Title", null, "active", false, "feature", "main", "succeeded",
                "Jin", "repo-1", "web", [], [], "abc", "Proj", ProjectId: "guid"));
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task VoteAsync(string project, string repositoryId, int id, PrVote vote, CancellationToken ct) => Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repositoryId, int id, int threadId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonAsync(string project, string repositoryId, int id, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string project, string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct) => Task.CompletedTask;
        public Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string projectId, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PolicyEvaluation>>(
            [
                new PolicyEvaluation("Build validation", "approved", true),
                new PolicyEvaluation("Minimum reviewers", "rejected", true),
                new PolicyEvaluation("Comment resolution", "queued", false),
            ]);
    }

    [Fact]
    public async Task PrDialog_Renders_Policies_Section_With_Pass_Fail_Pending_Glyphs()
    {
        var vm = new PrDetailViewModel(new PolicyRenderStore(), 42);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new PrDetailDialog(App, vm, NoopTextInput(), _ => { });
        detail.Build();

        var text = detail.Body.Text;
        Assert.Contains("Policies:", text);
        Assert.Contains("✓ Build validation (blocking)", text);
        Assert.Contains("✗ Minimum reviewers (blocking)", text);
        Assert.Contains("⧗ Comment resolution", text);
        // A non-blocking policy carries no blocking marker.
        Assert.DoesNotContain("Comment resolution (blocking)", text);
    }

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "My Project",
    };

    [Fact]
    public async Task PrDialog_Gb_Opens_The_Branch_Url()
    {
        var vm = new PrDetailViewModel(new PolicyRenderStore(), 42);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new PrDetailDialog(App, vm, NoopTextInput(), _ => { }, context: Context);
        string? captured = null;
        detail.OpenUrlAction = url => captured = url;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('b'));

        Assert.NotNull(captured);
        Assert.Contains("_git/", captured);
        Assert.Contains("version=GB", captured);
    }

    [Fact]
    public async Task PrDialog_Gb_Without_Context_Does_Not_Throw_Or_Open()
    {
        var vm = new PrDetailViewModel(new PolicyRenderStore(), 42);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new PrDetailDialog(App, vm, NoopTextInput(), _ => { }); // no context
        var opened = false;
        detail.OpenUrlAction = _ => opened = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        var ex = Record.Exception(() =>
        {
            dialog.NewKeyDownEvent(new Key('g'));
            dialog.NewKeyDownEvent(new Key('b'));
        });

        Assert.Null(ex);
        Assert.False(opened);
    }

    // ---- PR detail: text entry via ITextInput (ADR 0020) ----
    //
    // These drive the real 'c' / 'gc' / 'x' / 'u' keys (no seam), so the requests are
    // captured by the fake exactly as the real dialog issues them. The dialog's vm has no
    // PullRequest loaded (FakePrStore, no LoadAsync), so the mutation call it hands off to
    // (ReplyAsync/AddPrCommentAsync/...) early-returns before touching app.Invoke — the
    // requests are unaffected either way, since they're read before the mutation is called.

    [Fact]
    public void PrDialog_C_Reads_The_ThreadId_Prompt_Then_The_Reply_Text()
    {
        var textInput = new FakeTextInput("3", "looks good");
        var detail = NewPrDialog(textInput);
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.Equal(2, textInput.Requests.Count);
        Assert.Equal("reply to thread #", textInput.Requests[0].Title);
        Assert.True(textInput.Requests[0].SingleLine);
        Assert.Equal("reply", textInput.Requests[1].Title);
        Assert.False(textInput.Requests[1].SingleLine);
    }

    [Fact]
    public void PrDialog_C_Cancelled_ThreadId_Prompt_Never_Asks_For_Reply_Text()
    {
        var textInput = new FakeTextInput((string?)null); // cancels the thread-id prompt
        var detail = NewPrDialog(textInput);
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.Single(textInput.Requests); // never reached the reply-text prompt
    }

    [Fact]
    public void PrDialog_Gc_Reads_Via_TextInput_For_The_PR_Comment()
    {
        var textInput = new FakeTextInput("nice work");
        var detail = NewPrDialog(textInput);
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('c'));

        var request = Assert.Single(textInput.Requests);
        Assert.Equal("PR comment", request.Title);
        Assert.False(request.SingleLine);
    }

    [Fact]
    public void PrDialog_X_Reads_The_ThreadId_Prompt_For_Resolve()
    {
        var textInput = new FakeTextInput("5");
        var detail = NewPrDialog(textInput);
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('x'));

        var request = Assert.Single(textInput.Requests);
        Assert.Equal("resolve thread #", request.Title);
        Assert.True(request.SingleLine);
    }

    [Fact]
    public void PrDialog_U_Reads_The_ThreadId_Prompt_For_Reactivate()
    {
        var textInput = new FakeTextInput("5");
        var detail = NewPrDialog(textInput);
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('u'));

        var request = Assert.Single(textInput.Requests);
        Assert.Equal("reactivate thread #", request.Title);
        Assert.True(request.SingleLine);
    }

    // ---- Work-item detail ----

    private sealed class FakeWorkItemStore : IWorkItemStore
    {
        public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) => throw new NotSupportedException();
    }

    private static WorkItemDetailDialog NewWorkItemDialog()
    {
        var vm = new WorkItemDetailViewModel(new FakeWorkItemStore(), 7);
        return new WorkItemDetailDialog(App, vm, NoopEditor(), _ => { });
    }

    [Fact]
    public void WorkItemDialog_Focuses_The_TextView_Body()
    {
        var detail = NewWorkItemDialog();
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        Assert.IsType<TextView>(dialog.Focused);
    }

    [Fact]
    public void WorkItemDialog_Q_Closes_Even_When_TextView_Has_Focus()
    {
        var detail = NewWorkItemDialog();
        var closed = false;
        detail.CloseAction = () => closed = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('q'));

        Assert.True(closed);
    }

    [Fact]
    public void WorkItemDialog_C_Reaches_The_Comment_Seam()
    {
        var detail = NewWorkItemDialog();
        var commented = false;
        detail.CommentAction = () => commented = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.True(commented);
    }

    [Fact]
    public void WorkItemDialog_T_Reaches_The_Tags_Seam()
    {
        var detail = NewWorkItemDialog();
        var tagged = false;
        detail.TagsAction = () => tagged = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('t'));

        Assert.True(tagged);
    }

    [Fact]
    public void WorkItemDialog_PageDown_Still_Scrolls_The_Body()
    {
        var detail = NewWorkItemDialog();
        var dialog = detail.Build();
        dialog.Layout(new Size(60, 10));
        detail.Body.Text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        dialog.SetFocus();

        var before = detail.Body.Viewport.Y;
        dialog.NewKeyDownEvent(Key.PageDown);

        Assert.True(detail.Body.Viewport.Y > before,
            $"expected scroll to advance from {before}, got {detail.Body.Viewport.Y}");
    }

    private static WorkItemDetailDialog LaidOutWorkItemDialog(out Terminal.Gui.Views.Dialog dialog)
    {
        var detail = NewWorkItemDialog();
        dialog = detail.Build();
        dialog.Layout(new Size(60, 12));
        detail.Body.Text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        dialog.SetFocus();
        return detail;
    }

    [Fact]
    public void WorkItemDialog_J_Moves_Body_Down_One_Row()
    {
        var detail = LaidOutWorkItemDialog(out var dialog);

        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(1, detail.Body.CurrentRow);
    }

    [Fact]
    public void WorkItemDialog_Count_Then_J_Moves_By_Count()
    {
        var detail = LaidOutWorkItemDialog(out var dialog);

        dialog.NewKeyDownEvent(new Key('5'));
        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(5, detail.Body.CurrentRow);
    }

    [Fact]
    public void WorkItemDialog_G_And_gg_Jump_To_Ends()
    {
        var detail = LaidOutWorkItemDialog(out var dialog);
        var maxTop = detail.Body.Lines - detail.Body.Viewport.Height; // pager: top of the last page

        dialog.NewKeyDownEvent(new Key('G'));
        Assert.Equal(maxTop, detail.Body.CurrentRow);

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('g'));
        Assert.Equal(0, detail.Body.CurrentRow);
    }

    [Fact]
    public void WorkItemDialog_Esc_With_Pending_Count_Clears_It_Without_Closing()
    {
        var detail = NewWorkItemDialog();
        var closed = false;
        detail.CloseAction = () => closed = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('5')); // start a count
        dialog.NewKeyDownEvent(Key.Esc);      // should clear the count, not close

        Assert.False(closed);

        dialog.NewKeyDownEvent(Key.Esc);      // nothing pending now → closes

        Assert.True(closed);
    }

    [Fact]
    public void WorkItemDialog_Question_Mark_Opens_The_Help_Seam()
    {
        var detail = NewWorkItemDialog();
        var opened = false;
        detail.HelpAction = () => opened = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('?'));

        Assert.True(opened);
    }

    // ---- unit E: injected remap table reaches the modal dialogs ----

    [Fact]
    public void PrDialog_Router_Responds_To_A_Remapped_Key_From_The_Injected_Table()
    {
        // open-diff remapped d -> z in the PR-detail scope. The dialog builds its router from the
        // injected table, so 'z' must reach the diff-open seam (proving the remap reaches modals,
        // not just the shell's own keybar).
        var keys = new KeysConfig(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            ["pullrequestdetail"] = new Dictionary<string, IReadOnlyList<string>> { ["open-diff"] = new[] { "z" } },
        });
        var vm = new PrDetailViewModel(new FakePrStore(), 42);
        var detail = new PrDetailDialog(App, vm, NoopTextInput(), _ => { }, bindings: KeyBindingTable.FromConfig(keys));
        var opened = false;
        detail.DiffAction = () => opened = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('z'));

        Assert.True(opened);
    }
}
