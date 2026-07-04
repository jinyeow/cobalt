using Cobalt.Tui.Input;

namespace Cobalt.Tui.Tests.Input;

public class KeymapRouterTests
{
    private static KeymapRouter Router() => new(KeyBindingTable.Default());

    [Fact]
    public void Single_Key_Matches_Global_Command()
    {
        var result = Router().Feed("j", KeyScope.Global);

        Assert.Equal(KeyResultKind.Matched, result.Kind);
        Assert.Equal(AppCommand.MoveDown, result.Command);
    }

    [Fact]
    public void Sequence_gg_Requires_Two_Keys()
    {
        var router = Router();

        var first = router.Feed("g", KeyScope.Global);
        var second = router.Feed("g", KeyScope.Global);

        Assert.Equal(KeyResultKind.Pending, first.Kind);
        Assert.Equal(KeyResultKind.Matched, second.Kind);
        Assert.Equal(AppCommand.MoveTop, second.Command);
    }

    [Fact]
    public void Sequence_gx_Shares_Prefix_With_gg()
    {
        var router = Router();

        router.Feed("g", KeyScope.Global);
        var result = router.Feed("x", KeyScope.Global);

        Assert.Equal(AppCommand.OpenInBrowser, result.Command);
    }

    [Fact]
    public void Escape_Clears_Pending_Sequence()
    {
        var router = Router();

        router.Feed("g", KeyScope.Global);
        var esc = router.Feed("Esc", KeyScope.Global);
        var g = router.Feed("g", KeyScope.Global);

        Assert.Equal(KeyResultKind.None, esc.Kind);
        Assert.Equal(KeyResultKind.Pending, g.Kind); // fresh sequence, not a completed gg
    }

    [Fact]
    public void Unmatched_Key_After_Prefix_Resets_And_Reports_NoMatch()
    {
        var router = Router();

        router.Feed("g", KeyScope.Global);
        var result = router.Feed("z", KeyScope.Global);

        Assert.Equal(KeyResultKind.None, result.Kind);
        // and the buffer is clear: 'g' starts a new sequence again
        Assert.Equal(KeyResultKind.Pending, router.Feed("g", KeyScope.Global).Kind);
    }

    [Fact]
    public void Scoped_Command_Only_Matches_In_Its_Scope()
    {
        var router = Router();

        var inList = router.Feed("v", KeyScope.PullRequestDetail);
        var global = Router().Feed("v", KeyScope.Global);

        Assert.Equal(AppCommand.Vote, inList.Command);
        Assert.Equal(KeyResultKind.None, global.Kind);
    }

    [Fact]
    public void Scope_Falls_Back_To_Global_Bindings()
    {
        var result = Router().Feed("j", KeyScope.WorkItemList);

        Assert.Equal(AppCommand.MoveDown, result.Command);
    }

    [Fact]
    public void Capital_G_Is_Distinct_From_Lowercase_Sequences()
    {
        var result = Router().Feed("G", KeyScope.Global);

        Assert.Equal(AppCommand.MoveBottom, result.Command);
    }

    [Fact]
    public void Control_Keys_Route()
    {
        Assert.Equal(AppCommand.HalfPageDown, Router().Feed("C-d", KeyScope.Global).Command);
        Assert.Equal(AppCommand.HalfPageUp, Router().Feed("C-u", KeyScope.Global).Command);
    }

    [Fact]
    public void Colon_Opens_Palette_And_Question_Mark_Help()
    {
        Assert.Equal(AppCommand.CommandPalette, Router().Feed(":", KeyScope.Global).Command);
        Assert.Equal(AppCommand.Help, Router().Feed("?", KeyScope.Global).Command);
    }
}
