using Cobalt.Core.Models;
using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

public class PrRowFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static PullRequest Pr(
        int id,
        string title = "title",
        string repo = "web",
        string project = "Core",
        bool draft = false,
        int ageDays = 3) =>
        new(id, title, null, "active", draft, "feature", "main", "succeeded", "Jin", "r1", repo,
            [], [], "abc", project, Now - TimeSpan.FromDays(ageDays));

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void Row_Never_Exceeds_And_Fills_Width(int width)
    {
        var rows = new[] { Pr(1, "a short title", "web"), Pr(2, "another", "api") };
        var cols = PrColumns.For(rows);

        foreach (var pr in rows)
        {
            var text = PrRowFormatter.Format(pr, width, cols, Now, comments: null);
            Assert.Equal(width, text.Length); // padded to fill so the highlight spans the row
        }
    }

    [Fact]
    public void Repo_Column_Shows_Full_Name_When_It_Fits()
    {
        var rows = new[] { Pr(1, "t", "payments-service"), Pr(2, "t", "web") };
        var cols = PrColumns.For(rows);

        var text = PrRowFormatter.Format(rows[0], 200, cols, Now, null);

        Assert.Contains("payments-service", text);
        Assert.DoesNotContain("…", text); // wide terminal: nothing truncated
    }

    [Fact]
    public void Title_Expands_To_Fill_On_Wide_Terminals()
    {
        var longTitle = new string('x', 90);
        var rows = new[] { Pr(1, longTitle, "web") };
        var cols = PrColumns.For(rows);

        var narrow = PrRowFormatter.Format(rows[0], 80, cols, Now, null);
        var wide = PrRowFormatter.Format(rows[0], 200, cols, Now, null);

        // At 80 the 90-char title cannot fit and is truncated; at 200 it shows in full.
        Assert.Contains("…", narrow);
        Assert.Contains(longTitle, wide);
        Assert.DoesNotContain("…", wide);
    }

    [Fact]
    public void Ellipsis_Only_When_Title_Does_Not_Fit()
    {
        var rows = new[] { Pr(1, "fits fine", "web") };
        var cols = PrColumns.For(rows);

        var text = PrRowFormatter.Format(rows[0], 120, cols, Now, null);

        Assert.DoesNotContain("…", text);
    }

    [Fact]
    public void Project_Column_Present_Only_When_Rows_Span_Multiple_Projects()
    {
        // Org scope: rows span two projects -> project column appears.
        var mixed = new[] { Pr(1, "t", "web", "Core"), Pr(2, "t", "api", "Payments") };
        var mixedCols = PrColumns.For(mixed);
        Assert.True(mixedCols.ShowProject);
        var mixedText = PrRowFormatter.Format(mixed[0], 200, mixedCols, Now, null);
        Assert.Contains("Core", mixedText);

        // Project scope: a single project -> no project column, and its name is absent.
        var single = new[] { Pr(1, "t", "web", "Core"), Pr(2, "t", "api", "Core") };
        var singleCols = PrColumns.For(single);
        Assert.False(singleCols.ShowProject);
        var singleText = PrRowFormatter.Format(single[0], 200, singleCols, Now, null);
        Assert.DoesNotContain("Core", singleText);
    }

    [Fact]
    public void Age_Column_Reflects_Creation_Date()
    {
        var rows = new[] { Pr(1, "t", "web", ageDays: 3) };
        var cols = PrColumns.For(rows);

        var text = PrRowFormatter.Format(rows[0], 120, cols, Now, null);

        Assert.Contains("3d", text);
    }

    [Fact]
    public void Comment_Badge_Appears_When_Count_Known_And_Omitted_Otherwise()
    {
        var rows = new[] { Pr(1, "t", "web") };
        var cols = PrColumns.For(rows);

        var without = PrRowFormatter.Format(rows[0], 120, cols, Now, comments: null);
        var with = PrRowFormatter.Format(rows[0], 120, cols, Now, comments: 5);

        Assert.DoesNotContain("💬", without);
        Assert.Contains("💬 5", with);
        Assert.Equal(120, with.Length);
    }
}
