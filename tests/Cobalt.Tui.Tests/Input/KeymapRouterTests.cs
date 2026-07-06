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

    // ---- count prefixes ----

    [Fact]
    public void Single_Digit_Is_Pending_Then_Verb_Carries_Count()
    {
        var router = Router();

        var five = router.Feed("5", KeyScope.Global);
        var j = router.Feed("j", KeyScope.Global);

        Assert.Equal(KeyResultKind.Pending, five.Kind);
        Assert.Equal(KeyResultKind.Matched, j.Kind);
        Assert.Equal(AppCommand.MoveDown, j.Command);
        Assert.Equal(5, j.Count);
    }

    [Fact]
    public void Multi_Digit_Count_Accumulates()
    {
        var router = Router();

        router.Feed("1", KeyScope.Global);
        router.Feed("0", KeyScope.Global);
        var j = router.Feed("j", KeyScope.Global);

        Assert.Equal(AppCommand.MoveDown, j.Command);
        Assert.Equal(10, j.Count);
    }

    [Fact]
    public void Bare_Zero_Is_Not_A_Count()
    {
        var router = Router();

        var zero = router.Feed("0", KeyScope.Global);
        var j = router.Feed("j", KeyScope.Global);

        Assert.Equal(KeyResultKind.None, zero.Kind);
        Assert.Equal(AppCommand.MoveDown, j.Command);
        Assert.Null(j.Count);
    }

    [Fact]
    public void Count_Applies_To_G()
    {
        var router = Router();

        router.Feed("5", KeyScope.Global);
        var g = router.Feed("G", KeyScope.Global);

        Assert.Equal(AppCommand.MoveBottom, g.Command);
        Assert.Equal(5, g.Count);

        var plain = Router().Feed("G", KeyScope.Global);
        Assert.Equal(AppCommand.MoveBottom, plain.Command);
        Assert.Null(plain.Count);
    }

    [Fact]
    public void Count_Applies_To_gg_Sequence()
    {
        var router = Router();

        router.Feed("5", KeyScope.Global);
        router.Feed("g", KeyScope.Global);
        var gg = router.Feed("g", KeyScope.Global);

        Assert.Equal(AppCommand.MoveTop, gg.Command);
        Assert.Equal(5, gg.Count);
    }

    [Fact]
    public void Escape_Mid_Count_Clears_It()
    {
        var router = Router();

        router.Feed("5", KeyScope.Global);
        router.Feed("Esc", KeyScope.Global);
        var j = router.Feed("j", KeyScope.Global);

        Assert.Equal(AppCommand.MoveDown, j.Command);
        Assert.Null(j.Count);
    }

    [Fact]
    public void Count_Then_Unmatched_Key_Clears_Count()
    {
        var router = Router();

        router.Feed("5", KeyScope.Global);
        var z = router.Feed("z", KeyScope.Global);
        var j = router.Feed("j", KeyScope.Global);

        Assert.Equal(KeyResultKind.None, z.Kind);
        Assert.Equal(AppCommand.MoveDown, j.Command);
        Assert.Null(j.Count);
    }

    [Fact]
    public void Count_Applies_To_Control_Chord()
    {
        var router = Router();

        router.Feed("5", KeyScope.Global);
        var cd = router.Feed("C-d", KeyScope.Global);

        Assert.Equal(AppCommand.HalfPageDown, cd.Command);
        Assert.Equal(5, cd.Count);
    }

    [Fact]
    public void Digit_Mid_Sequence_Is_Not_A_Count()
    {
        // 'g' then '1' is the g1 direct-jump binding, not a count.
        var router = Router();

        router.Feed("g", KeyScope.Global);
        var g1 = router.Feed("1", KeyScope.Global);

        Assert.Equal(AppCommand.SectionWorkItems, g1.Command);
        Assert.Null(g1.Count);
    }

    [Fact]
    public void Section_Cycling_And_Direct_Jump_Bindings()
    {
        Assert.Equal(AppCommand.NextSection, Feed2("g", "t"));
        Assert.Equal(AppCommand.PrevSection, Feed2("g", "T"));
        Assert.Equal(AppCommand.SectionWorkItems, Feed2("g", "1"));
        Assert.Equal(AppCommand.SectionPullRequests, Feed2("g", "2"));
    }

    [Fact]
    public void Digits_No_Longer_Switch_Sections_Directly()
    {
        // Freed for counts: a lone '1'/'2' is a pending count, not a section jump.
        Assert.Equal(KeyResultKind.Pending, Router().Feed("1", KeyScope.Global).Kind);
        Assert.Equal(KeyResultKind.Pending, Router().Feed("2", KeyScope.Global).Kind);
    }

    private static AppCommand Feed2(string a, string b)
    {
        var router = Router();
        router.Feed(a, KeyScope.Global);
        return router.Feed(b, KeyScope.Global).Command;
    }

    // ---- dialog verbs ----

    [Fact]
    public void PullRequestDetail_Adds_Diff_Complete_Abandon()
    {
        Assert.Equal(AppCommand.OpenDiff, Router().Feed("d", KeyScope.PullRequestDetail).Command);
        Assert.Equal(AppCommand.CompletePr, Router().Feed("C", KeyScope.PullRequestDetail).Command);
        Assert.Equal(AppCommand.AbandonPr, Router().Feed("A", KeyScope.PullRequestDetail).Command);
    }

    [Fact]
    public void DiffReview_Adds_File_Nav_And_Pane_Cycle()
    {
        Assert.Equal(AppCommand.NextFile, Router().Feed("]", KeyScope.DiffReview).Command);
        Assert.Equal(AppCommand.PrevFile, Router().Feed("[", KeyScope.DiffReview).Command);
        // Scoped Tab shadows the global NextTab (scoped bindings enumerate first).
        Assert.Equal(AppCommand.CyclePane, Router().Feed("Tab", KeyScope.DiffReview).Command);
        Assert.Equal(AppCommand.NextTab, Router().Feed("Tab", KeyScope.Global).Command);
    }

    [Fact]
    public void Count_Applies_To_Diff_File_Nav()
    {
        var router = Router();
        router.Feed("3", KeyScope.DiffReview);
        var next = router.Feed("]", KeyScope.DiffReview);

        Assert.Equal(AppCommand.NextFile, next.Command);
        Assert.Equal(3, next.Count);
    }
}
