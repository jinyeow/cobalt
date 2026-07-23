using System.Text.Json;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class WorkItemDetailFormatterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    public async Task Full_Tier_Is_The_Dialog_Golden_At_Any_Width(int width)
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct);

        // DescriptionMarkdown carries platform newlines (HtmlMarkdown), which the dialog's
        // TextView normalizes on display — compare the normalized text, as the dialog shows it.
        Assert.Equal(
            DetailFormatterFixture.WorkItemFullGolden,
            WorkItemDetailFormatter.Render(vm, width, PreviewTier.Full).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Summary_Tier_Clamp_Never_Splits_A_Surrogate_Pair()
    {
        // The comment line starts "  " (2 units), then 2-unit emoji pairs: at width 6
        // the naive cut point (width - 1 = 5) lands between a pair's halves.
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct, store =>
            store.Comments =
            [
                new Cobalt.Core.Models.WorkItemComment(
                    1, string.Concat(Enumerable.Repeat("😀", 10)),
                    new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero), "x"),
            ]);

        var lines = WorkItemDetailFormatter.Render(vm, 6, PreviewTier.Summary).Split('\n');

        Assert.Contains("  😀…", lines);
    }

    [Fact]
    public async Task Summary_Tier_Truncates_The_Description_To_A_Three_Line_Head_With_Ellipsis()
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct);

        var text = WorkItemDetailFormatter.Render(vm, 120, PreviewTier.Summary);

        Assert.Contains("── Description ──\nCache misses spike when tokens expire.\n\nRepro: run the soak harness for an hour.\n…", text);
        Assert.DoesNotContain("Expected: one refresh per expiry.", text);
    }

    [Fact]
    public async Task Summary_Tier_Keeps_The_Total_Count_But_Only_The_Latest_Two_Comments()
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct);

        var text = WorkItemDetailFormatter.Render(vm, 120, PreviewTier.Summary);

        Assert.Contains("── Comments (3) ──", text);
        Assert.DoesNotContain("Alice Anderson (2026-07-01)", text);
        Assert.Contains("Bob Brown (2026-07-02)", text);
        Assert.Contains("Jin Puah (2026-07-03)", text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(30)]
    public async Task Summary_Tier_Clamps_Every_Line_To_The_Width_With_A_Trailing_Ellipsis(int width)
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct);

        var lines = WorkItemDetailFormatter.Render(vm, width, PreviewTier.Summary).Split('\n');

        Assert.All(lines, l => Assert.True(l.Length <= width, $"'{l}' exceeds {width}"));
        // The comment lines are longer than both widths, so they must have been clamped.
        var commentLine = Assert.Single(lines, l => l.StartsWith("  Jin Puah (2026-07-03)", StringComparison.Ordinal));
        Assert.EndsWith("…", commentLine);
        Assert.Equal(width, commentLine.Length);
    }

    [Fact]
    public async Task Full_Tier_Without_Description_Or_Comments_Renders_Empty_And_A_Zero_Count()
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(Ct, store =>
        {
            var fields = new Dictionary<string, JsonElement>
            {
                ["System.Title"] = JsonDocument.Parse("\"Bare item\"").RootElement.Clone(),
                ["System.State"] = JsonDocument.Parse("\"New\"").RootElement.Clone(),
                ["System.WorkItemType"] = JsonDocument.Parse("\"Task\"").RootElement.Clone(),
            };
            store.Item = new Cobalt.Core.Models.WorkItem(8, fields);
            store.Comments = [];
        });

        var text = WorkItemDetailFormatter.Render(vm, 120, PreviewTier.Full);

        Assert.Contains("── Description ──\n(empty)", text);
        Assert.Contains("── Comments (0) ──", text);
        Assert.Contains("Assigned: (unassigned)", text);
    }
}
