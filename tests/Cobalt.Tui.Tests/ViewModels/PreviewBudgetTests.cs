using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The preview pane's vertical budget (#48): the H2 formatters clamp Summary output
/// horizontally but are vertically unbounded, so the pane caps rendered lines to its
/// height with an omission marker instead of relying on TextView clipping.
/// </summary>
public class PreviewBudgetTests
{
    [Fact]
    public void Over_Budget_Keeps_The_Head_And_Counts_The_Dropped_Lines()
    {
        // 5 lines into a 3-line budget: 2 head lines, so 3 lines are dropped.
        var fitted = PreviewBudget.Fit("a\nb\nc\nd\ne", 3);

        Assert.Equal("a\nb\n… 3 more", fitted);
    }

    [Theory]
    [InlineData(4)] // under budget
    [InlineData(3)] // exactly at budget
    public void At_Or_Under_Budget_Returns_The_Text_Unchanged(int maxLines)
    {
        Assert.Equal("a\nb\nc", PreviewBudget.Fit("a\nb\nc", maxLines));
    }

    [Fact]
    public void A_Zero_Budget_Means_No_Budget_Is_Known_And_Leaves_The_Text_Alone()
    {
        // An unlaid-out pane reports Viewport.Height = 0; truncating to nothing there would
        // hide content that the very next layout pass has room for.
        Assert.Equal("a\nb\nc", PreviewBudget.Fit("a\nb\nc", 0));
    }

    [Fact]
    public void A_One_Line_Budget_Is_The_Omission_Line_Alone()
    {
        // One row of room: the marker is the only thing that fits, and it accounts for
        // every line (nothing was shown).
        Assert.Equal("… 4 more", PreviewBudget.Fit("a\nb\nc\nd", 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void Empty_Text_Is_Returned_Unchanged_At_Any_Budget(int maxLines)
    {
        Assert.Equal("", PreviewBudget.Fit("", maxLines));
    }

    [Fact]
    public void A_Trailing_Newline_Counts_As_A_Line()
    {
        // "a\nb\n" is three rendered rows (the last one blank), so a 2-line budget drops two.
        Assert.Equal("a\n… 2 more", PreviewBudget.Fit("a\nb\n", 2));
    }
}
