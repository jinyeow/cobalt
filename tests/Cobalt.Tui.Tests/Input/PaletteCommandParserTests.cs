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
    public void Log_Opens_The_Operations_Log()
    {
        Assert.Equal(PaletteActionKind.Log, PaletteCommandParser.Parse("log").Kind);
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

    [Theory]
    [InlineData("done", "")]
    [InlineData("done show", "show")]
    [InlineData("done hide", "hide")]
    public void Done_Forms_Parse_As_ToggleDone(string input, string expectedArg)
    {
        var action = PaletteCommandParser.Parse(input);

        Assert.Equal(PaletteActionKind.ToggleDone, action.Kind);
        Assert.Equal(expectedArg, action.Argument);
    }

    [Fact]
    public void Project_With_Name_Sets_Project_Filter()
    {
        var action = PaletteCommandParser.Parse("project Fabrikam");

        Assert.Equal(PaletteActionKind.SetProjectFilter, action.Kind);
        Assert.Equal("Fabrikam", action.Argument);
    }

    [Fact]
    public void Bare_Project_Sets_Project_Filter_With_Empty_Argument()
    {
        var action = PaletteCommandParser.Parse("project");

        Assert.Equal(PaletteActionKind.SetProjectFilter, action.Kind);
        Assert.Equal("", action.Argument);
    }

    [Fact]
    public void Theme_With_Value_Sets_Theme()
    {
        var action = PaletteCommandParser.Parse("theme light");

        Assert.Equal(PaletteActionKind.SetTheme, action.Kind);
        Assert.Equal("light", action.Argument);
    }

    [Fact]
    public void Bare_Theme_Sets_Theme_With_Empty_Argument()
    {
        var action = PaletteCommandParser.Parse("theme");

        Assert.Equal(PaletteActionKind.SetTheme, action.Kind);
        Assert.Equal("", action.Argument);
    }

    [Fact]
    public void Preview_With_Value_Sets_Preview()
    {
        var action = PaletteCommandParser.Parse("preview off");

        Assert.Equal(PaletteActionKind.SetPreview, action.Kind);
        Assert.Equal("off", action.Argument);
    }

    [Fact]
    public void Bare_Preview_Sets_Preview_With_Empty_Argument()
    {
        var action = PaletteCommandParser.Parse("preview");

        Assert.Equal(PaletteActionKind.SetPreview, action.Kind);
        Assert.Equal("", action.Argument);
    }

    [Fact]
    public void Preview_Value_Is_Passed_Through_Unvalidated()
    {
        // Validation (and its message) belongs to the view-model, exactly as :theme does.
        var action = PaletteCommandParser.Parse("preview bogus");

        Assert.Equal(PaletteActionKind.SetPreview, action.Kind);
        Assert.Equal("bogus", action.Argument);
    }
}
