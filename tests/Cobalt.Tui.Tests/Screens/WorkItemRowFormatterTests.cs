using System.Text.Json;
using Cobalt.Core.Models;
using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

public class WorkItemRowFormatterTests
{
    private static WorkItem Wi(long id, string title, string project = "") =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El($"\"{title}\""),
            ["System.State"] = El("\"Active\""),
            ["System.WorkItemType"] = El("\"Bug\""),
            ["System.IterationPath"] = El("\"Project\\\\Sprint 42\""),
            ["System.TeamProject"] = El($"\"{project}\""),
        });

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void Row_Is_Padded_To_Width(int width)
    {
        var text = WorkItemRowFormatter.Format(Wi(123, "fix the thing"), width, default);
        Assert.Equal(width, text.Length);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(200)]
    public void Row_Is_Padded_To_Width_With_Project_Column(int width)
    {
        var rows = new[] { Wi(1, "a", "Core"), Wi(2, "b", "Payments") };
        var cols = WorkItemColumns.For(rows);
        var text = WorkItemRowFormatter.Format(rows[0], width, cols);
        Assert.Equal(width, text.Length);
    }

    [Fact]
    public void Title_Expands_On_Wide_Terminals_And_Truncates_When_Narrow()
    {
        var longTitle = new string('x', 120);
        var wide = WorkItemRowFormatter.Format(Wi(1, longTitle), 200, default);
        var narrow = WorkItemRowFormatter.Format(Wi(1, longTitle), 80, default);

        Assert.Contains(longTitle, wide);
        Assert.DoesNotContain("…", wide);
        Assert.Contains("…", narrow);
    }

    [Fact]
    public void Shows_Id_Type_State_And_Iteration()
    {
        var text = WorkItemRowFormatter.Format(Wi(4242, "t"), 120, default);
        Assert.Contains("4242", text);
        Assert.Contains("Bug", text);
        Assert.Contains("Active", text);
        Assert.Contains("Sprint 42", text);
    }

    [Fact]
    public void Project_Column_Present_Only_When_Rows_Span_Multiple_Projects()
    {
        // Org scope: rows span two projects -> project column appears and the name shows.
        var mixed = new[] { Wi(1, "t", "Core"), Wi(2, "t", "Payments") };
        var mixedCols = WorkItemColumns.For(mixed);
        Assert.True(mixedCols.ShowProject);
        Assert.Contains("Core", WorkItemRowFormatter.Format(mixed[0], 200, mixedCols));

        // Single project (or project scope): no column, and the name is absent.
        var single = new[] { Wi(1, "t", "Core"), Wi(2, "t", "Core") };
        var singleCols = WorkItemColumns.For(single);
        Assert.False(singleCols.ShowProject);
        Assert.DoesNotContain("Core", WorkItemRowFormatter.Format(single[0], 200, singleCols));
    }
}
