using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrDetailFormatterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    public async Task Full_Tier_Is_The_Dialog_Golden_At_Any_Width(int width)
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct);

        Assert.Equal(DetailFormatterFixture.PrFullGolden, PrDetailFormatter.Render(vm, width, PreviewTier.Full));
    }

    [Fact]
    public async Task Summary_Tier_Keeps_The_Thread_Count_But_Drops_The_Thread_Listing()
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct);

        var text = PrDetailFormatter.Render(vm, 120, PreviewTier.Summary);

        Assert.Contains("── Threads (1 unresolved) ──", text);
        Assert.DoesNotContain("#1 [Active]", text);
        Assert.DoesNotContain("Alice Anderson: Should this retry be capped?", text);
    }

    [Fact]
    public async Task Summary_Tier_Truncates_The_Description_To_A_Three_Line_Head_With_Ellipsis()
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct);

        var text = PrDetailFormatter.Render(vm, 120, PreviewTier.Summary);

        Assert.Contains("── Description ──\nAdds a bounded retry to the token cache.\n\nMotivation: transient AAD hiccups\n…", text);
        Assert.DoesNotContain("should not surface as auth failures.", text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(30)]
    public async Task Summary_Tier_Clamps_Every_Line_To_The_Width_With_A_Trailing_Ellipsis(int width)
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct);

        var lines = PrDetailFormatter.Render(vm, width, PreviewTier.Summary).Split('\n');

        Assert.All(lines, l => Assert.True(l.Length <= width, $"'{l}' exceeds {width}"));
        // The branch/status line is longer than both widths, so it must have been clamped.
        var branchLine = Assert.Single(lines, l => l.StartsWith("cobalt: ", StringComparison.Ordinal));
        Assert.EndsWith("…", branchLine);
        Assert.Equal(width, branchLine.Length);
    }

    [Fact]
    public async Task Summary_Tier_Clamp_Never_Splits_A_Surrogate_Pair()
    {
        // "!42  " is 5 UTF-16 units, then 2-unit emoji pairs: at width 9 the naive
        // cut point (width - 1 = 8) lands between a pair's halves.
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct, store =>
            store.Pr = store.Pr with { Title = string.Concat(Enumerable.Repeat("😀", 20)) });

        var lines = PrDetailFormatter.Render(vm, 9, PreviewTier.Summary).Split('\n');

        var titleLine = Assert.Single(lines, l => l.StartsWith("!42", StringComparison.Ordinal));
        Assert.Equal("!42  😀…", titleLine);
    }

    [Fact]
    public async Task Full_Tier_Without_Reviewers_Or_Description_Renders_None_And_No_Description_Section()
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(Ct, store =>
            store.Pr = store.Pr with { Reviewers = [], Description = null });

        var text = PrDetailFormatter.Render(vm, 120, PreviewTier.Full);

        Assert.Contains("Reviewers:\n  (none)", text);
        Assert.DoesNotContain("── Description ──", text);
    }
}
