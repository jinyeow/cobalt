using System.Text.Json;
using Cobalt.Core.Ado;

namespace Cobalt.Core.Tests.Ado;

public class JsonPatchBuilderTests
{
    [Fact]
    public void SetField_Emits_Add_Op_Under_Fields_Path()
    {
        var json = new JsonPatchBuilder()
            .SetField("System.State", "Active")
            .ToJson();

        using var doc = JsonDocument.Parse(json);
        var op = doc.RootElement[0];
        Assert.Equal("add", op.GetProperty("op").GetString());
        Assert.Equal("/fields/System.State", op.GetProperty("path").GetString());
        Assert.Equal("Active", op.GetProperty("value").GetString());
    }

    [Fact]
    public void SetField_Numeric_Serializes_As_Number()
    {
        var json = new JsonPatchBuilder()
            .SetField("Microsoft.VSTS.Scheduling.StoryPoints", 5.0)
            .ToJson();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Number, doc.RootElement[0].GetProperty("value").ValueKind);
        Assert.Equal(5.0, doc.RootElement[0].GetProperty("value").GetDouble());
    }

    [Fact]
    public void Multiple_Fields_Preserve_Order()
    {
        var json = new JsonPatchBuilder()
            .SetField("System.Title", "New title")
            .SetField("System.Tags", "a; b")
            .ToJson();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("/fields/System.Title", doc.RootElement[0].GetProperty("path").GetString());
        Assert.Equal("/fields/System.Tags", doc.RootElement[1].GetProperty("path").GetString());
    }

    [Fact]
    public void RemoveField_Emits_Remove_Op_Without_Value()
    {
        var json = new JsonPatchBuilder()
            .RemoveField("System.AssignedTo")
            .ToJson();

        using var doc = JsonDocument.Parse(json);
        var op = doc.RootElement[0];
        Assert.Equal("remove", op.GetProperty("op").GetString());
        Assert.False(op.TryGetProperty("value", out _));
    }

    [Fact]
    public void Empty_Builder_Reports_HasOperations_False()
    {
        Assert.False(new JsonPatchBuilder().HasOperations);
        Assert.True(new JsonPatchBuilder().SetField("System.Title", "x").HasOperations);
    }

    [Fact]
    public void String_Values_Are_Escaped()
    {
        var json = new JsonPatchBuilder()
            .SetField("System.Title", "quote \" and \\ backslash")
            .ToJson();

        using var doc = JsonDocument.Parse(json); // must be valid JSON
        Assert.Equal("quote \" and \\ backslash", doc.RootElement[0].GetProperty("value").GetString());
    }
}
