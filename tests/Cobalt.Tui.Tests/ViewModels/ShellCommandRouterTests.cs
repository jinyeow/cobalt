using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The shell's section- and workspace-aware routing (#76): which screen a command targets, and
/// which of them the shell must decline so its verb switch still sees them. Headless — the router
/// is UI-free, so the decisions the shell used to bury in Terminal.Gui side effects are plain
/// unit tests here.
/// </summary>
public class ShellCommandRouterTests
{
    private static ShellCommandRouter Router(
        WorkspaceViewModel workspace,
        AppSection section = AppSection.WorkItems,
        bool prListBuilt = true) =>
        new(workspace, () => section, () => prListBuilt);

    [Fact]
    public void Tab_Cycles_Pane_Focus_While_The_Preview_Shows()
    {
        var workspace = new WorkspaceViewModel();
        workspace.SetPreviewVisible(true);

        var action = Router(workspace).Route(AppCommand.CyclePane, null);

        Assert.Equal(ShellActionKind.ApplyWorkspaceFocus, action.Kind);
        Assert.Equal(WorkspacePane.Preview, workspace.FocusedPane);
    }

    [Fact]
    public void Tab_Falls_Back_To_NextTab_While_The_Preview_Is_Hidden()
    {
        var workspace = new WorkspaceViewModel(); // preview hidden

        var action = Router(workspace).Route(AppCommand.CyclePane, 3);

        // Declined, and rewritten to NextTab: the shell's fall-through must see the rewritten
        // command (its section toggle), with the count still attached.
        Assert.Equal(ShellActionKind.NotRouted, action.Kind);
        Assert.Equal(AppCommand.NextTab, action.Command);
        Assert.Equal(3, action.Count);
    }

    [Theory]
    [InlineData(AppCommand.NextTab, ShellActionKind.PrNextTab)]
    [InlineData(AppCommand.PrevTab, ShellActionKind.PrPrevTab)]
    public void Tab_Cycles_The_Pr_SubTabs_In_The_Pr_Section(AppCommand command, ShellActionKind expected)
    {
        var action = Router(new WorkspaceViewModel(), AppSection.PullRequests).Route(command, null);

        Assert.Equal(expected, action.Kind);
    }

    [Theory]
    [InlineData(AppCommand.NextTab)]
    [InlineData(AppCommand.PrevTab)]
    public void Tab_Still_Toggles_Sections_When_The_Pr_List_Was_Never_Built(AppCommand command)
    {
        // No connection → the PR screen is a placeholder, so the sub-tab intercept must not
        // swallow Tab; it gates on "is the list built", not on the section alone.
        var action = Router(new WorkspaceViewModel(), AppSection.PullRequests, prListBuilt: false)
            .Route(command, null);

        Assert.Equal(ShellActionKind.NotRouted, action.Kind);
        Assert.Equal(command, action.Command);
    }

    [Fact]
    public void FocusLeft_Moves_Focus_Back_To_The_List()
    {
        var workspace = new WorkspaceViewModel();
        workspace.SetPreviewVisible(true);
        workspace.FocusRight();

        var action = Router(workspace).Route(AppCommand.FocusLeft, null);

        Assert.Equal(ShellActionKind.ApplyWorkspaceFocus, action.Kind);
        Assert.Equal(WorkspacePane.List, workspace.FocusedPane);
    }

    [Fact]
    public void FocusRight_Moves_Focus_To_The_Preview()
    {
        var workspace = new WorkspaceViewModel();
        workspace.SetPreviewVisible(true);

        var action = Router(workspace).Route(AppCommand.FocusRight, null);

        Assert.Equal(ShellActionKind.ApplyWorkspaceFocus, action.Kind);
        Assert.Equal(WorkspacePane.Preview, workspace.FocusedPane);
    }

    [Theory]
    [InlineData(AppCommand.FocusLeft)]
    [InlineData(AppCommand.FocusRight)]
    public void An_Edge_Focus_Move_Is_Declined_So_The_Key_Keeps_Its_Other_Meaning(AppCommand command)
    {
        // Preview hidden: neither direction changes anything, so the shell falls through and the
        // key behaves exactly as it did before the workspace existed.
        var action = Router(new WorkspaceViewModel()).Route(command, null);

        Assert.Equal(ShellActionKind.NotRouted, action.Kind);
        Assert.Equal(command, action.Command);
    }

    [Fact]
    public void Movement_Scrolls_The_Preview_While_It_Holds_Focus()
    {
        var workspace = new WorkspaceViewModel();
        workspace.SetPreviewVisible(true);
        workspace.FocusRight();

        var action = Router(workspace).Route(AppCommand.MoveDown, 5);

        Assert.Equal(ShellActionKind.ScrollPreview, action.Kind);
        Assert.Equal(5, action.Count);
    }

    [Theory]
    [InlineData(AppSection.WorkItems, ShellActionKind.NavigateWorkItemList)]
    [InlineData(AppSection.PullRequests, ShellActionKind.NavigatePrList)]
    public void Movement_Moves_The_Active_Sections_List_Cursor(AppSection section, ShellActionKind expected)
    {
        var action = Router(new WorkspaceViewModel(), section).Route(AppCommand.HalfPageDown, 2);

        Assert.Equal(expected, action.Kind);
        Assert.Equal(AppCommand.HalfPageDown, action.Command);
        Assert.Equal(2, action.Count);
    }

    [Theory]
    [InlineData(AppSection.WorkItems, ShellActionKind.RefreshWorkItemList)]
    [InlineData(AppSection.PullRequests, ShellActionKind.RefreshPrList)]
    public void Refresh_Reloads_The_Visible_Section_Only(AppSection section, ShellActionKind expected)
    {
        var action = Router(new WorkspaceViewModel(), section).Route(AppCommand.Refresh, null);

        Assert.Equal(expected, action.Kind);
    }

    [Theory]
    [InlineData(AppSection.WorkItems, ShellActionKind.OpenWorkItem)]
    [InlineData(AppSection.PullRequests, ShellActionKind.OpenPr)]
    public void Open_Targets_The_Visible_Sections_Selection(AppSection section, ShellActionKind expected)
    {
        var action = Router(new WorkspaceViewModel(), section).Route(AppCommand.Open, null);

        Assert.Equal(expected, action.Kind);
    }

    [Fact]
    public void Filtering_Starts_In_The_Work_Item_Section()
    {
        var action = Router(new WorkspaceViewModel(), AppSection.WorkItems).Route(AppCommand.FilterStart, null);

        Assert.Equal(ShellActionKind.StartWorkItemFilter, action.Kind);
    }

    [Fact]
    public void Filtering_Is_Silently_Consumed_In_The_Pr_Section()
    {
        // The PR list has no filter prompt, and `/` there has always done nothing *quietly*.
        // Consumed, not NotRouted: declining would newly surface "'/' not available here".
        var action = Router(new WorkspaceViewModel(), AppSection.PullRequests).Route(AppCommand.FilterStart, null);

        Assert.Equal(ShellActionKind.Consumed, action.Kind);
    }

    [Theory]
    [InlineData(AppCommand.Vote)]
    [InlineData(AppCommand.Comment)]
    [InlineData(AppCommand.ChangeState)]
    [InlineData(AppCommand.YankId)]
    [InlineData(AppCommand.OpenInBrowser)]
    [InlineData(AppCommand.CommandPalette)]
    [InlineData(AppCommand.Quit)]
    [InlineData(AppCommand.Help)]
    public void Commands_With_No_Section_Branching_Are_Left_To_The_Shell(AppCommand command)
    {
        // Deliberately narrow: the router owns only the arms that branch on section or workspace
        // state, so these verbs keep exactly the shell's existing handling — including that Vote
        // and the work-item verbs are NOT section-gated today.
        var action = Router(new WorkspaceViewModel()).Route(command, null);

        Assert.Equal(ShellActionKind.NotRouted, action.Kind);
        Assert.Equal(command, action.Command);
    }
}
