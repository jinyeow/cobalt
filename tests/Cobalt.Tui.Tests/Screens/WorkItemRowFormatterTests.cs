using System.Text.Json;
using Cobalt.Core.Models;
using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

public class WorkItemRowFormatterTests
{
    private static WorkItem Wi(long id, string title) =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El($"\"{title}\""),
            ["System.State"] = El("\"Active\""),
            ["System.WorkItemType"] = El("\"Bug\""),
            ["System.IterationPath"] = El("\"Project\\\\Sprint 42\""),
        });

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void Row_Is_Padded_To_Width(int width)
    {
        var text = WorkItemRowFormatter.Format(Wi(123, "fix the thing"), width);
        Assert.Equal(width, text.Length);
    }

    [Fact]
    public void Title_Expands_On_Wide_Terminals_And_Truncates_When_Narrow()
    {
        var longTitle = new string('x', 120);
        var wide = WorkItemRowFormatter.Format(Wi(1, longTitle), 200);
        var narrow = WorkItemRowFormatter.Format(Wi(1, longTitle), 80);

        Assert.Contains(longTitle, wide);
        Assert.DoesNotContain("…", wide);
        Assert.Contains("…", narrow);
    }

    [Fact]
    public void Shows_Id_Type_State_And_Iteration()
    {
        var text = WorkItemRowFormatter.Format(Wi(4242, "t"), 120);
        Assert.Contains("4242", text);
        Assert.Contains("Bug", text);
        Assert.Contains("Active", text);
        Assert.Contains("Sprint 42", text);
    }
}
