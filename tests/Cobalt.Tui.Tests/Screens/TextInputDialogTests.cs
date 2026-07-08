using System.Drawing;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: builds the in-TUI text-entry overlay and drives keys through the real
/// Terminal.Gui routing (no driver / <c>Init()</c>). Guards submit (Enter), the newline chord,
/// cancel (Esc), the Ctrl+E <c>$EDITOR</c> hatch, and single-line behaviour. <c>resolve</c> is a
/// recording seam so "did/did not submit" is observable without a run loop.
/// </summary>
public class TextInputDialogTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly Func<string, CancellationToken, Task<string?>> NoEditor =
        (_, _) => Task.FromResult<string?>(null);

    private static (TextInputDialog view, Dialog dialog, List<string?> resolved) Built(
        TextInputRequest request,
        Func<string, CancellationToken, Task<string?>>? editor = null)
    {
        var resolved = new List<string?>();
        var view = new TextInputDialog(
            App,
            request,
            editor ?? NoEditor,
            resolved.Add,
            CancellationToken.None,
            post: a => a()); // synchronous marshal so the Ctrl+E refill is observable
        var dialog = view.Build();
        dialog.Layout(new Size(80, 24));
        dialog.SetFocus();
        return (view, dialog, resolved);
    }

    private static void Type(Dialog dialog, string text)
    {
        foreach (var ch in text)
        {
            dialog.NewKeyDownEvent(new Key(ch));
        }
    }

    [Fact]
    public void Typed_Runes_Accumulate_In_The_Editable_Field()
    {
        var (view, dialog, _) = Built(new TextInputRequest("comment"));

        Type(dialog, "hi");

        Assert.Equal("hi", view.Text);
    }

    [Fact]
    public void Enter_Submits_The_Typed_Text()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "ship it");

        dialog.NewKeyDownEvent(new Key(KeyCode.Enter));

        Assert.Equal(["ship it"], resolved); // submitted exactly once with the buffer, no trailing newline
    }

    [Fact]
    public void Newline_Chord_Inserts_A_Newline_And_Does_Not_Submit()
    {
        var (view, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "a");

        // Ctrl+J is delivered by the dotnet/ansi driver as Enter|CtrlMask (see TextInputDialog remarks).
        dialog.NewKeyDownEvent(new Key(KeyCode.Enter).WithCtrl);
        Type(dialog, "b");

        Assert.Equal("a\nb", view.Text.ReplaceLineEndings("\n")); // TextView reconstructs Text with the platform newline
        Assert.Empty(resolved); // the chord did NOT submit
    }

    [Fact]
    public void Win32_CtrlJ_Form_Also_Inserts_A_Newline()
    {
        var (view, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "a");

        // Defensive path: the Win32 driver may deliver a physical Ctrl+J as KeyCode.J|CtrlMask.
        dialog.NewKeyDownEvent(new Key('j').WithCtrl);
        Type(dialog, "b");

        Assert.Equal("a\nb", view.Text.ReplaceLineEndings("\n")); // TextView reconstructs Text with the platform newline
        Assert.Empty(resolved);
    }

    [Fact]
    public void Esc_Cancels_With_Null()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "draft");

        dialog.NewKeyDownEvent(new Key(KeyCode.Esc));

        Assert.Equal([null], resolved);
    }

    [Fact]
    public void CtrlE_Hands_Current_Buffer_To_Editor_And_Refills_From_Result()
    {
        string? seen = null;
        var (view, dialog, resolved) = Built(
            new TextInputRequest("comment", Initial: "half"),
            editor: (buffer, _) =>
            {
                seen = buffer;
                return Task.FromResult<string?>("full text");
            });

        dialog.NewKeyDownEvent(new Key('e').WithCtrl);

        Assert.Equal("half", seen);        // the hatch got the current buffer
        Assert.Equal("full text", view.Text); // and the field was refilled from its result
        Assert.Empty(resolved);            // returning to the field, NOT auto-submitting
    }

    [Fact]
    public void CtrlE_Null_Result_Leaves_Buffer_And_Does_Not_Submit()
    {
        var (view, dialog, resolved) = Built(
            new TextInputRequest("comment", Initial: "keep"),
            editor: (_, _) => Task.FromResult<string?>(null));

        dialog.NewKeyDownEvent(new Key('e').WithCtrl);

        Assert.Equal("keep", view.Text); // unchanged
        Assert.Empty(resolved);          // not submitted
    }

    [Fact]
    public void SingleLine_Enter_Submits()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("assignee", SingleLine: true));
        Type(dialog, "jin");

        dialog.NewKeyDownEvent(new Key(KeyCode.Enter));

        Assert.Equal(["jin"], resolved);
    }

    [Fact]
    public void SingleLine_Field_Is_A_TextField_Not_A_TextView()
    {
        var (view, _, _) = Built(new TextInputRequest("assignee", SingleLine: true));

        Assert.IsType<TextField>(view.Field);
    }
}
