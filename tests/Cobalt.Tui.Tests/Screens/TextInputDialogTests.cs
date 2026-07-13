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
    public void Enter_On_An_Empty_Buffer_Submits_The_Empty_String_Not_Null()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("comment"));

        dialog.NewKeyDownEvent(new Key(KeyCode.Enter)); // no typing first

        // Distinct from Esc's [null]: WorkItemActions.AssignAsync's empty-submit guard depends on
        // empty-Enter producing "" (a submit of nothing), not null (a cancel).
        Assert.Equal([""], resolved);
    }

    [Fact]
    public void Shift_Enter_Chord_Inserts_A_Newline_And_Does_Not_Submit()
    {
        var (view, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "a");

        // The kitty-keyboard-protocol path: a terminal that delivers Shift+Enter distinctly.
        dialog.NewKeyDownEvent(new Key(KeyCode.Enter).WithShift);
        Type(dialog, "b");

        Assert.Equal("a\nb", view.Text.ReplaceLineEndings("\n"));
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
    public void CtrlE_While_An_Editor_Is_Open_Ignores_A_Second_Press()
    {
        var launches = 0;
        var gate = new TaskCompletionSource<string?>();
        var (_, dialog, _) = Built(
            new TextInputRequest("comment", Initial: "half"),
            editor: (_, _) =>
            {
                launches++;
                return gate.Task; // editor stays "open" until the test releases it
            });

        dialog.NewKeyDownEvent(new Key('e').WithCtrl); // opens $EDITOR (task pending)
        dialog.NewKeyDownEvent(new Key('e').WithCtrl); // re-press while it's still open

        Assert.Equal(1, launches); // the in-flight guard blocks the concurrent re-entry

        gate.SetResult("done"); // release the first editor (cleanup)
    }

    [Fact]
    public void CtrlE_After_The_First_Completes_Can_Launch_Again()
    {
        var launches = 0;
        var (_, dialog, resolved) = Built(
            new TextInputRequest("comment", Initial: "x"),
            editor: (_, _) =>
            {
                launches++;
                return Task.FromResult<string?>("edited");
            });

        dialog.NewKeyDownEvent(new Key('e').WithCtrl); // completes synchronously, returns to the field
        dialog.NewKeyDownEvent(new Key('e').WithCtrl); // legitimate re-escalation (ADR 0020)

        Assert.Equal(2, launches); // sequential re-escalation still works — the guard does not over-block
        Assert.Empty(resolved);
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
    public void SingleLine_CtrlEnter_Submits_The_Newline_Guard_Is_Skipped()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("assignee", SingleLine: true));
        Type(dialog, "jin");

        // The newline chord is gated on !SingleLine, so Ctrl+Enter is not a chord here — it falls
        // through to submit, exactly like plain Enter.
        dialog.NewKeyDownEvent(new Key(KeyCode.Enter).WithCtrl);

        Assert.Equal(["jin"], resolved);
    }

    [Fact]
    public void SingleLine_CtrlJ_Does_Not_Insert_A_Newline()
    {
        var (view, dialog, resolved) = Built(new TextInputRequest("assignee", SingleLine: true));
        Type(dialog, "jin");

        // Ctrl+J is a chord only for multi-line; on a single-line field the guard is skipped, so it
        // must not reach InsertNewline (which casts to TextView) — no newline, no crash.
        dialog.NewKeyDownEvent(new Key('j').WithCtrl);

        Assert.DoesNotContain('\n', view.Text);
        Assert.Empty(resolved);
    }

    [Fact]
    public void SingleLine_Field_Is_A_TextField_Not_A_TextView()
    {
        var (view, _, _) = Built(new TextInputRequest("assignee", SingleLine: true));

        Assert.IsType<TextField>(view.Field);
    }

    [Fact]
    public void CtrlE_Launch_Failure_Surfaces_In_The_Hint()
    {
        var (view, dialog, resolved) = Built(
            new TextInputRequest("comment", Initial: "keep"),
            editor: (_, _) => throw new EditorLaunchException("could not start editor 'nvim'"));

        dialog.NewKeyDownEvent(new Key('e').WithCtrl);

        Assert.Contains("couldn't open $EDITOR", view.Hint); // the failure is not swallowed
        Assert.Contains("could not start editor", view.Hint);
        Assert.Equal("keep", view.Text); // buffer preserved
        Assert.Empty(resolved);          // not submitted
    }

    [Fact]
    public void Enter_Submits_Only_Once_Even_On_A_Repeat()
    {
        var (_, dialog, resolved) = Built(new TextInputRequest("comment"));
        Type(dialog, "x");

        dialog.NewKeyDownEvent(new Key(KeyCode.Enter));
        dialog.NewKeyDownEvent(new Key(KeyCode.Enter)); // a queued/repeat key after close

        Assert.Equal(["x"], resolved); // resolved exactly once
    }

    [Fact]
    public void Keys_After_Close_Are_Ignored()
    {
        string? seen = null;
        var (_, dialog, resolved) = Built(
            new TextInputRequest("comment", Initial: "keep"),
            editor: (b, _) => { seen = b; return Task.FromResult<string?>("edited"); });

        dialog.NewKeyDownEvent(new Key(KeyCode.Esc));  // cancel/close
        dialog.NewKeyDownEvent(new Key('e').WithCtrl); // Ctrl+E after close must be ignored

        Assert.Null(seen);              // the editor hatch was not invoked after close
        Assert.Equal([null], resolved); // still just the single cancel
    }
}
