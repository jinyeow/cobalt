using Cobalt.Tui.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Headless `:` palette Tab-completion wiring (unit E): the shell feeds the field text through
/// <see cref="PaletteSuggestionsViewModel"/> on Tab/S-Tab and writes the completion back into the
/// field. Drives the palette field directly with <c>NewKeyDownEvent</c>, the same way the detail
/// dialogs' key-delivery tests do.
/// </summary>
public class PaletteCompletionTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly Key ShiftTab = new(KeyCode.Tab | KeyCode.ShiftMask);

    private static CobaltShell NewShell(out ShellViewModel vm, params string[] contexts)
    {
        vm = new ShellViewModel(contexts.Length == 0 ? ["work"] : contexts, "work");
        return new CobaltShell(App, vm);
    }

    [Fact]
    public void Tab_Completes_A_Command_Prefix_Preserving_The_Leading_Colon()
    {
        using var shell = NewShell(out _);
        var field = shell.OpenPaletteForTest();
        field.Text = ":th";

        field.NewKeyDownEvent(Key.Tab);

        Assert.Equal(":theme", field.Text.ToString());
    }

    [Fact]
    public void Tab_Cycles_Real_Context_Names_For_The_Context_Argument()
    {
        using var shell = NewShell(out _, "work", "web");
        var field = shell.OpenPaletteForTest();
        field.Text = "context ";

        field.NewKeyDownEvent(Key.Tab);
        Assert.Equal("context work", field.Text.ToString());

        field.NewKeyDownEvent(Key.Tab);
        Assert.Equal("context web", field.Text.ToString());

        field.NewKeyDownEvent(Key.Tab); // wraps back to the first
        Assert.Equal("context work", field.Text.ToString());
    }

    [Fact]
    public void Shift_Tab_Completes_A_Fresh_Field_To_The_Last_Context_Name()
    {
        using var shell = NewShell(out _, "work", "web");
        var field = shell.OpenPaletteForTest();
        field.Text = "context ";

        field.NewKeyDownEvent(ShiftTab);

        Assert.Equal("context web", field.Text.ToString());
    }

    [Fact]
    public void Tab_Chains_From_An_Arg_Command_Into_Argument_Completion_Not_Stale_Commands()
    {
        using var shell = NewShell(out _, "work", "web");
        var field = shell.OpenPaletteForTest();
        field.Text = ":c";

        // :c -> :context (with the trailing space Accept adds for an arg-taking command).
        field.NewKeyDownEvent(Key.Tab);
        Assert.Equal(":context ", field.Text.ToString());

        // The 2nd Tab must chain into CONTEXT NAMES (not re-cycle command matches to :scope).
        field.NewKeyDownEvent(Key.Tab);
        Assert.Equal(":context work", field.Text.ToString());

        // Further Tabs cycle the argument candidates.
        field.NewKeyDownEvent(Key.Tab);
        Assert.Equal(":context web", field.Text.ToString());
    }
}
