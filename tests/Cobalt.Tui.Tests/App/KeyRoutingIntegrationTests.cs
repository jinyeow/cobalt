using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// End-to-end for the input glue minus Terminal.Gui's event delivery: real Key
/// objects → KeyTokenizer → KeymapRouter → ShellViewModel dispatch. This is the
/// path CobaltShell.WireKeys runs on every keystroke.
/// </summary>
public class KeyRoutingIntegrationTests
{
    private sealed class Harness
    {
        private readonly KeymapRouter _router = new(KeyBindingTable.Default());
        public ShellViewModel Vm { get; } = new(["work", "oss"], "work");
        public List<AppCommand> Unhandled { get; } = [];

        public KeyScope Scope => Vm.ActiveSection == AppSection.WorkItems
            ? KeyScope.WorkItemList
            : KeyScope.PullRequestList;

        public void Press(Key key)
        {
            var token = KeyTokenizer.ToToken(key);
            if (token is null)
            {
                return;
            }
            var result = _router.Feed(token, Scope);
            if (result.Kind != KeyResultKind.Matched)
            {
                return;
            }
            if (!Vm.HandleCommand(result.Command))
            {
                Unhandled.Add(result.Command);
            }
        }
    }

    [Fact]
    public void Number_Keys_Switch_Sections()
    {
        var h = new Harness();

        h.Press(new Key('2'));
        Assert.Equal(AppSection.PullRequests, h.Vm.ActiveSection);

        h.Press(new Key('1'));
        Assert.Equal(AppSection.WorkItems, h.Vm.ActiveSection);
    }

    [Fact]
    public void Colon_Q_Enter_Sequence_Requests_Quit()
    {
        var h = new Harness();
        var quit = false;
        h.Vm.QuitRequested += () => quit = true;

        // ':' opens palette in the shell; here it surfaces as an unhandled CommandPalette
        h.Press(new Key(':'));
        Assert.Contains(AppCommand.CommandPalette, h.Unhandled);

        // the shell then routes typed palette text; simulate that directly
        h.Vm.HandlePaletteInput("q");
        Assert.True(quit);
    }

    [Fact]
    public void GG_Sequence_Produces_MoveTop()
    {
        var h = new Harness();

        h.Press(new Key('g'));
        h.Press(new Key('g'));

        // MoveTop is a screen command (not handled by the shell VM) → recorded unhandled
        Assert.Contains(AppCommand.MoveTop, h.Unhandled);
    }

    [Fact]
    public void Tab_Cycles_Sections()
    {
        var h = new Harness();

        h.Press(Key.Tab);

        Assert.Equal(AppSection.PullRequests, h.Vm.ActiveSection);
    }
}
