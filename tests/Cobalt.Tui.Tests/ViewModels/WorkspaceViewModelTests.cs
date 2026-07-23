using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// Pane focus + movement routing for the list+preview workspace (ADR 0024 fork C):
/// focus is workspace state owned by the view-model, never inferred from Terminal.Gui
/// focus, so every routing decision is testable headlessly. No Terminal.Gui.
/// </summary>
public class WorkspaceViewModelTests
{
    [Fact]
    public void Starts_With_List_Focused_And_Preview_Hidden()
    {
        var vm = new WorkspaceViewModel();

        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
        Assert.False(vm.PreviewVisible);
    }

    [Fact]
    public void FocusRight_While_The_Preview_Is_Hidden_Is_A_NoOp()
    {
        var vm = new WorkspaceViewModel();

        Assert.False(vm.FocusRight());
        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
    }

    [Fact]
    public void CyclePane_While_The_Preview_Is_Hidden_Signals_Fallback()
    {
        var vm = new WorkspaceViewModel();

        // false = not consumed: the caller falls back to today's Tab semantics
        // (ADR 0024 — below the collapse threshold the UX is exactly today's).
        Assert.False(vm.CyclePane());
        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
    }

    [Fact]
    public void FocusRight_Then_FocusLeft_Moves_Focus_While_The_Preview_Is_Visible()
    {
        var vm = new WorkspaceViewModel();
        vm.SetPreviewVisible(true);

        Assert.True(vm.FocusRight());
        Assert.Equal(WorkspacePane.Preview, vm.FocusedPane);

        Assert.True(vm.FocusLeft());
        Assert.Equal(WorkspacePane.List, vm.FocusedPane);

        // Already at the list edge: no change to report.
        Assert.False(vm.FocusLeft());
        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
    }

    [Fact]
    public void CyclePane_While_The_Preview_Is_Visible_Toggles_The_Focused_Pane()
    {
        var vm = new WorkspaceViewModel();
        vm.SetPreviewVisible(true);

        Assert.True(vm.CyclePane());
        Assert.Equal(WorkspacePane.Preview, vm.FocusedPane);

        Assert.True(vm.CyclePane());
        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
    }

    [Fact]
    public void Collapsing_The_Preview_Forces_Focus_Back_To_The_List()
    {
        var vm = new WorkspaceViewModel();
        vm.SetPreviewVisible(true);
        Assert.True(vm.FocusRight());

        // The invariant: FocusedPane == Preview implies PreviewVisible, so a collapse
        // (resize below the threshold, :preview off) can never strand focus on a hidden pane.
        vm.SetPreviewVisible(false);

        Assert.Equal(WorkspacePane.List, vm.FocusedPane);
    }

    [Theory]
    [InlineData(AppCommand.MoveDown)]
    [InlineData(AppCommand.MoveUp)]
    [InlineData(AppCommand.HalfPageDown)]
    public void Route_Follows_The_Focused_Pane(AppCommand command)
    {
        var vm = new WorkspaceViewModel();

        // List focus keeps movement on the list cursor (ADR 0024)...
        Assert.Equal(WorkspaceKeyRoute.ListCursor, vm.Route(command));

        vm.SetPreviewVisible(true);
        vm.FocusRight();

        // ...preview focus routes the same keys to preview scroll.
        Assert.Equal(WorkspaceKeyRoute.PreviewScroll, vm.Route(command));
    }
}
