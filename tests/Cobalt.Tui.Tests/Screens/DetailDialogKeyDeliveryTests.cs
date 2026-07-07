using System.Drawing;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.Editor;
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

    private static PrDetailDialog NewPrDialog()
    {
        var vm = new PrDetailViewModel(new FakePrStore(), 42);
        return new PrDetailDialog(App, vm, NoopEditor(), _ => { });
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
}
