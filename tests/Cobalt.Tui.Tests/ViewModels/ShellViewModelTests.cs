using Cobalt.Core.Config;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class ShellViewModelTests
{
    private static ShellViewModel Vm() => new(
        contextNames: ["work", "oss"],
        initialContext: "work");

    [Fact]
    public void Starts_On_WorkItems_In_Initial_Context()
    {
        var vm = Vm();

        Assert.Equal(AppSection.WorkItems, vm.ActiveSection);
        Assert.Equal("work", vm.ContextName);
    }

    [Fact]
    public void Section_Commands_Switch_Sections()
    {
        var vm = Vm();
        var changes = 0;
        vm.SectionChanged += () => changes++;

        vm.HandleCommand(AppCommand.SectionPullRequests);
        Assert.Equal(AppSection.PullRequests, vm.ActiveSection);

        vm.HandleCommand(AppCommand.SectionWorkItems);
        Assert.Equal(AppSection.WorkItems, vm.ActiveSection);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void NextTab_Cycles_Sections()
    {
        var vm = Vm();

        vm.HandleCommand(AppCommand.NextTab);
        Assert.Equal(AppSection.PullRequests, vm.ActiveSection);

        vm.HandleCommand(AppCommand.NextTab);
        Assert.Equal(AppSection.WorkItems, vm.ActiveSection);
    }

    [Fact]
    public void Palette_Quit_Raises_QuitRequested()
    {
        var vm = Vm();
        var quit = false;
        vm.QuitRequested += () => quit = true;

        vm.HandlePaletteInput("q");

        Assert.True(quit);
    }

    [Fact]
    public void Back_Command_Quits_From_The_Main_View()
    {
        var vm = Vm();
        var quit = false;
        vm.QuitRequested += () => quit = true;

        var handled = vm.HandleCommand(AppCommand.Back);

        Assert.True(handled);
        Assert.True(quit);
    }

    [Fact]
    public void StatusLine_Shows_Full_Word_Context()
    {
        var vm = Vm();

        Assert.Contains("context:", vm.StatusLine);
        Assert.DoesNotContain("ctx:", vm.StatusLine);
    }

    [Fact]
    public void Palette_Ctx_Switches_To_Known_Context()
    {
        var vm = Vm();
        string? switched = null;
        vm.ContextSwitchRequested += name => switched = name;

        vm.HandlePaletteInput("ctx oss");

        Assert.Equal("oss", switched);
    }

    [Fact]
    public void Palette_Ctx_Unknown_Context_Logs_Error_And_Does_Not_Switch()
    {
        var vm = Vm();
        string? switched = null;
        vm.ContextSwitchRequested += name => switched = name;

        vm.HandlePaletteInput("ctx nope");

        Assert.Null(switched);
        Assert.Equal(MessageLevel.Error, vm.Messages.Current?.Level);
        Assert.Contains("nope", vm.Messages.Current?.Text);
    }

    [Fact]
    public void Palette_Unknown_Command_Logs_Error()
    {
        var vm = Vm();

        vm.HandlePaletteInput("frob");

        Assert.Equal(MessageLevel.Error, vm.Messages.Current?.Level);
    }

    [Fact]
    public void ContextSwitched_Updates_Status()
    {
        var vm = Vm();

        vm.OnContextSwitched("oss", "Jin");

        Assert.Equal("oss", vm.ContextName);
        Assert.Contains("oss", vm.StatusLine);
        Assert.Contains("Jin", vm.StatusLine);
    }

    [Fact]
    public void Default_Scope_Is_Org_And_Shown_In_Status()
    {
        var vm = Vm();

        Assert.Equal(PrScope.Org, vm.Scope);
        Assert.Contains("org", vm.StatusLine);
    }

    [Fact]
    public void Palette_Scope_Project_Flips_Scope_And_Raises_Event()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org);
        PrScope? requested = null;
        vm.ScopeChangeRequested += s => requested = s;

        vm.HandlePaletteInput("scope project");

        Assert.Equal(PrScope.Project, vm.Scope);
        Assert.Equal(PrScope.Project, requested);
        Assert.Contains("project", vm.StatusLine);
    }

    [Fact]
    public void Bare_Palette_Scope_Reports_Current_Without_Changing()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org);
        var raised = false;
        vm.ScopeChangeRequested += _ => raised = true;

        vm.HandlePaletteInput("scope");

        Assert.False(raised);
        Assert.Equal(PrScope.Org, vm.Scope);
        Assert.NotNull(vm.Messages.Current);
        Assert.Contains("org", vm.Messages.Current!.Text);
    }

    [Fact]
    public void Palette_Scope_Invalid_Value_Logs_Error()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org);

        vm.HandlePaletteInput("scope everywhere");

        Assert.Equal(MessageLevel.Error, vm.Messages.Current?.Level);
        Assert.Equal(PrScope.Org, vm.Scope);
    }

    [Fact]
    public void Default_Hides_Completed_Work_Items()
    {
        var vm = Vm();

        Assert.False(vm.IncludeCompletedWorkItems);
    }

    [Fact]
    public void Palette_Done_Show_Reveals_Completed_And_Raises_Event()
    {
        var vm = Vm();
        bool? raised = null;
        vm.DoneFilterChanged += include => raised = include;

        vm.HandlePaletteInput("done show");

        Assert.True(vm.IncludeCompletedWorkItems);
        Assert.True(raised);
    }

    [Fact]
    public void Palette_Done_Hide_Hides_Completed_And_Raises_Event()
    {
        var vm = Vm();
        vm.HandlePaletteInput("done show");
        bool? raised = null;
        vm.DoneFilterChanged += include => raised = include;

        vm.HandlePaletteInput("done hide");

        Assert.False(vm.IncludeCompletedWorkItems);
        Assert.False(raised);
    }

    [Fact]
    public void Bare_Palette_Done_Reports_Current_Without_Changing_Or_Raising()
    {
        var vm = Vm();
        var raised = false;
        vm.DoneFilterChanged += _ => raised = true;

        vm.HandlePaletteInput("done");

        Assert.False(raised);
        Assert.False(vm.IncludeCompletedWorkItems);
        Assert.NotNull(vm.Messages.Current);
    }

    [Fact]
    public void Palette_Done_Invalid_Value_Logs_Error()
    {
        var vm = Vm();

        vm.HandlePaletteInput("done maybe");

        Assert.Equal(MessageLevel.Error, vm.Messages.Current?.Level);
        Assert.False(vm.IncludeCompletedWorkItems);
    }

    [Fact]
    public void Palette_Project_Name_Sets_Filter_And_Raises_Event()
    {
        var vm = Vm();
        string? raised = "unset";
        vm.ProjectFilterChanged += p => raised = p;

        vm.HandlePaletteInput("project Fabrikam");

        Assert.Equal("Fabrikam", vm.ProjectFilter);
        Assert.Equal("Fabrikam", raised);
    }

    [Fact]
    public void Bare_Palette_Project_Clears_Active_Filter_And_Raises_Null()
    {
        var vm = Vm();
        vm.HandlePaletteInput("project Fabrikam");
        string? raised = "unset";
        vm.ProjectFilterChanged += p => raised = p;

        vm.HandlePaletteInput("project");

        Assert.Null(vm.ProjectFilter);
        Assert.Null(raised);
    }

    [Fact]
    public void Bare_Palette_Project_With_No_Filter_Reports_Without_Raising()
    {
        var vm = Vm();
        var raised = false;
        vm.ProjectFilterChanged += _ => raised = true;

        vm.HandlePaletteInput("project");

        Assert.False(raised);
        Assert.Null(vm.ProjectFilter);
        Assert.NotNull(vm.Messages.Current);
    }

    [Fact]
    public void Default_Theme_Is_Dark()
    {
        var vm = Vm();

        Assert.Equal(ThemeChoice.Dark, vm.CurrentTheme);
    }

    [Fact]
    public void Ctor_Accepts_Initial_Theme()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Org, ThemeChoice.Light);

        Assert.Equal(ThemeChoice.Light, vm.CurrentTheme);
    }

    [Fact]
    public void Bare_Palette_Theme_Reports_Current_Without_Changing_Or_Raising()
    {
        var vm = Vm();
        var raised = false;
        vm.ThemeChangeRequested += _ => raised = true;

        vm.HandlePaletteInput("theme");

        Assert.False(raised);
        Assert.Equal(ThemeChoice.Dark, vm.CurrentTheme);
        Assert.NotNull(vm.Messages.Current);
        Assert.Contains("dark", vm.Messages.Current!.Text);
    }

    [Fact]
    public void Palette_Theme_Light_Switches_Theme_And_Raises_Event()
    {
        var vm = Vm();
        ThemeChoice? requested = null;
        vm.ThemeChangeRequested += choice => requested = choice;

        vm.HandlePaletteInput("theme light");

        Assert.Equal(ThemeChoice.Light, vm.CurrentTheme);
        Assert.Equal(ThemeChoice.Light, requested);
    }

    [Fact]
    public void Palette_Theme_Is_Case_Insensitive()
    {
        var vm = Vm();

        vm.HandlePaletteInput("theme SYSTEM");

        Assert.Equal(ThemeChoice.System, vm.CurrentTheme);
    }

    [Fact]
    public void Palette_Theme_Invalid_Value_Logs_Error_Without_Throwing()
    {
        var vm = Vm();

        vm.HandlePaletteInput("theme rainbow");

        Assert.Equal(MessageLevel.Error, vm.Messages.Current?.Level);
        Assert.Equal(ThemeChoice.Dark, vm.CurrentTheme);
    }

    [Fact]
    public void Palette_Theme_System_Reissued_Resyncs_And_Raises_Again()
    {
        var vm = Vm();
        var raised = 0;
        vm.ThemeChangeRequested += _ => raised++;

        vm.HandlePaletteInput("theme system");
        vm.HandlePaletteInput("theme system");

        // `system` re-resolves against the live OS each time, so a repeat is a real refresh.
        Assert.Equal(2, raised);
        Assert.Equal(ThemeChoice.System, vm.CurrentTheme);
        Assert.NotNull(vm.Messages.Current);
        Assert.Contains("re-synced", vm.Messages.Current!.Text);
    }

    [Fact]
    public void Palette_Theme_Fixed_Reissued_Is_A_NoOp()
    {
        var vm = Vm();
        var raised = 0;
        vm.ThemeChangeRequested += _ => raised++;

        vm.HandlePaletteInput("theme light");
        vm.HandlePaletteInput("theme light");

        Assert.Equal(1, raised);
    }
}
