using Cobalt.Tui.Input;

namespace Cobalt.Tui.Tests.Input;

public class PaletteCommandParserTests
{
    [Theory]
    [InlineData("q")]
    [InlineData("quit")]
    [InlineData(":q")] // tolerate a typed leading colon
    public void Quit_Forms(string input)
    {
        Assert.Equal(PaletteActionKind.Quit, PaletteCommandParser.Parse(input).Kind);
    }

    [Fact]
    public void Ctx_With_Name_Switches_Context()
    {
        var action = PaletteCommandParser.Parse("ctx oss");

        Assert.Equal(PaletteActionKind.SwitchContext, action.Kind);
        Assert.Equal("oss", action.Argument);
    }

    [Fact]
    public void Ctx_Without_Name_Opens_Picker()
    {
        var action = PaletteCommandParser.Parse("ctx");

        Assert.Equal(PaletteActionKind.PickContext, action.Kind);
    }

    [Fact]
    public void Help_And_Messages()
    {
        Assert.Equal(PaletteActionKind.Help, PaletteCommandParser.Parse("help").Kind);
        Assert.Equal(PaletteActionKind.Messages, PaletteCommandParser.Parse("messages").Kind);
    }

    [Fact]
    public void Unknown_Command_Reports_Error_With_Input()
    {
        var action = PaletteCommandParser.Parse("frob");

        Assert.Equal(PaletteActionKind.Unknown, action.Kind);
        Assert.Contains("frob", action.Argument);
    }

    [Fact]
    public void Blank_Input_Is_Noop()
    {
        Assert.Equal(PaletteActionKind.None, PaletteCommandParser.Parse("   ").Kind);
    }

    [Fact]
    public void Scope_With_Value_Sets_Scope()
    {
        var action = PaletteCommandParser.Parse("scope org");

        Assert.Equal(PaletteActionKind.SetScope, action.Kind);
        Assert.Equal("org", action.Argument);
    }

    [Fact]
    public void Bare_Scope_Sets_Scope_With_Empty_Argument()
    {
        var action = PaletteCommandParser.Parse("scope");

        Assert.Equal(PaletteActionKind.SetScope, action.Kind);
        Assert.Equal("", action.Argument);
    }
}
