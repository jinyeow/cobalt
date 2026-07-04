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
}
