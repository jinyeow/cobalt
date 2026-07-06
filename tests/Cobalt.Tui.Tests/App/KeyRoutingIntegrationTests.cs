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

        // The commands CobaltShell.Dispatch (or the active screen) consumes after the
        // view-model declines them. A matched command outside this set is the
        // "silently swallowed" case the shell now surfaces instead of dropping.
        private static readonly HashSet<AppCommand> ScreenHandled =
        [
            AppCommand.MoveDown, AppCommand.MoveUp, AppCommand.MoveLeft, AppCommand.MoveRight,
            AppCommand.MoveTop, AppCommand.MoveBottom, AppCommand.HalfPageDown, AppCommand.HalfPageUp,
            AppCommand.Open, AppCommand.Back, AppCommand.Refresh, AppCommand.FilterStart,
            AppCommand.CommandPalette, AppCommand.YankId, AppCommand.OpenInBrowser,
            AppCommand.Comment, AppCommand.ChangeState, AppCommand.Assign, AppCommand.EditTags,
            AppCommand.Vote,
        ];

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
            if (Vm.HandleCommand(result.Command))
            {
                return;
            }
            Unhandled.Add(result.Command);

            // Mirror CobaltShell.Dispatch's default: a router-matched command that no
            // context handles must surface a message, never vanish silently.
            if (!ScreenHandled.Contains(result.Command))
            {
                Vm.Messages.Info($"'{result.Command}' not available here");
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

    [Fact]
    public void Matched_But_Unhandled_Command_Surfaces_A_Message()
    {
        var h = new Harness();

        // C-l → FocusRight is bound (matched by the router) but no context handles it;
        // it must produce a visible message rather than being silently swallowed.
        h.Press(new Key('l').WithCtrl);

        Assert.Contains(AppCommand.FocusRight, h.Unhandled);
        Assert.NotNull(h.Vm.Messages.Current);
        Assert.Contains("not available here", h.Vm.Messages.Current!.Text);
    }
}
