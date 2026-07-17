using Cobalt.Tui.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Headless shell chrome: the keybar renders from the live binding table and
/// follows the active section's scope (spec: lazygit-inspired redesign, stage A).
/// </summary>
public class ShellChromeTests
{
    private static readonly IApplication App = Application.Create();

    private static CobaltShell NewShell(out ShellViewModel vm)
    {
        vm = new ShellViewModel(["work"], "work");
        return new CobaltShell(App, vm);
    }

    [Fact]
    public void Keybar_Renders_On_Construction()
    {
        using var shell = NewShell(out _);

        Assert.Contains("j/k:move", shell.KeybarText);
        Assert.EndsWith("?:help", shell.KeybarText);
    }

    [Fact]
    public void Keybar_Follows_The_Active_Section_Scope()
    {
        using var shell = NewShell(out var vm);

        Assert.Contains("c:comment", shell.KeybarText); // work-item verbs

        vm.HandleCommand(Cobalt.Tui.Input.AppCommand.SectionPullRequests);

        Assert.Contains("v:vote", shell.KeybarText);    // PR verbs
        Assert.DoesNotContain("a:assign", shell.KeybarText);
    }

    [Fact]
    public void Showcmd_Renders_Pending_Count_And_Clears_On_Esc()
    {
        using var shell = NewShell(out _);

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key('5'));
        Assert.EndsWith("5 ", shell.StatusText);

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key('g'));
        Assert.EndsWith("5g ", shell.StatusText);

        shell.NewKeyDownEvent(Terminal.Gui.Input.Key.Esc);
        Assert.DoesNotContain("5g", shell.StatusText);
    }

    [Fact]
    public void Showcmd_Clears_When_The_Sequence_Completes()
    {
        using var shell = NewShell(out _);

        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key('3'));
        shell.NewKeyDownEvent(new Terminal.Gui.Input.Key('j'));

        Assert.DoesNotContain("3", shell.StatusText);
    }
}
