using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The shared matcher extracted from the palette's suggestion ranking, so the menu component
/// filters rows the same way `:` completes commands (one matcher, one behaviour).
/// </summary>
public class FuzzyFilterTests
{
    [Fact]
    public void IsSubsequence_Matches_Characters_In_Order_With_Gaps()
    {
        Assert.True(FuzzyFilter.IsSubsequence("cxt", "context"));
        Assert.False(FuzzyFilter.IsSubsequence("txc", "context"));
    }

    [Fact]
    public void IsSubsequence_Ignores_Case_On_Both_Sides()
    {
        Assert.True(FuzzyFilter.IsSubsequence("CTX", "context"));
        Assert.True(FuzzyFilter.IsSubsequence("ctx", "CONTEXT"));
    }

    [Fact]
    public void IsSubsequence_An_Empty_Query_Matches_Anything()
    {
        Assert.True(FuzzyFilter.IsSubsequence("", "context"));
        Assert.True(FuzzyFilter.IsSubsequence("", ""));
    }

    [Fact]
    public void Rank_Returns_The_Pool_Unchanged_For_An_Empty_Query()
    {
        string[] pool = ["theme", "context", "quit"];

        Assert.Equal(pool, FuzzyFilter.Rank(pool, ""));
    }

    [Fact]
    public void Rank_Puts_Prefix_Matches_Before_Subsequence_Matches_And_Drops_Non_Matches()
    {
        // "co" prefixes "context"; it is only a subsequence of "checkout"; "quit" has neither.
        string[] pool = ["checkout", "context", "quit"];

        Assert.Equal(["context", "checkout"], FuzzyFilter.Rank(pool, "co"));
    }
}
