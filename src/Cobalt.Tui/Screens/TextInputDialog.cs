using Cobalt.Tui.Editor;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// In-TUI text entry for short comments/replies (ADR 0020) — the instant, headless-testable
/// replacement for the <c>$EDITOR</c> suspend/resume handoff. A <see cref="Dialog"/> wrapping an
/// editable field (a multi-line <see cref="TextView"/>, or a <see cref="TextField"/> when
/// <see cref="TextInputRequest.SingleLine"/>) plus a one-line key hint. Built without a run loop
/// (mirrors <see cref="TextDialog"/>) so a view-level test can drive keys via
/// <c>NewKeyDownEvent</c>. <c>TuiTextInput</c> is the production <c>ITextInput</c> wrapper.
/// </summary>
/// <remarks>
/// Newline-chord probe (Terminal.Gui 2.4.16): the dotnet/ansi driver maps a physical Ctrl+J
/// (byte 0x0A) to <c>KeyCode.Enter | CtrlMask</c> (base <see cref="KeyCode.Enter"/>, Ctrl set) —
/// NOT <c>KeyCode.J | CtrlMask</c> — while plain Enter (CR, 0x0D) is <see cref="KeyCode.Enter"/>
/// with no modifier. So the newline chord is detected as "Enter with a Ctrl/Shift modifier"
/// (covering Ctrl+J and any terminal that delivers Ctrl/Shift+Enter distinctly, e.g. under the
/// kitty keyboard protocol), plus a defensive <c>KeyCode.J | CtrlMask</c> clause for the Win32
/// <c>windows</c> driver. Plain, unmodified Enter submits.
/// </remarks>
internal sealed class TextInputDialog
{
    private readonly TextInputRequest _request;
    private readonly Func<string, CancellationToken, Task<string?>> _openInEditor;
    private readonly Action<string?> _resolve;
    private readonly Action<Action> _post;
    // Linked to the flow's token; cancelled on close so a Ctrl+E editor call that is still queued
    // when the dialog closes (e.g. Ctrl+E then Esc) is skipped rather than launching over the
    // shell. Intentionally NOT disposed: the in-flight editor call still observes the token, and
    // GC reclaims the linked registration — disposing here would risk an ObjectDisposedException.
    private readonly CancellationTokenSource _dialogCts;
    private View? _field;
    private Label? _hint;
    // Set once, on the UI thread, at the first submit/cancel. Guards against a double-resolve from
    // queued keys and against Ctrl+E post-backs touching the disposed field after the dialog closed.
    private bool _closed;
    // In-flight guard for the Ctrl+E hatch: set on the UI thread when an $EDITOR call starts, cleared
    // when it completes. The suspend/resume handoff is deferred (queued via the UI-thread marshal), so
    // between the key press and the actual park the event loop is still live — a double-press would
    // otherwise queue a second $EDITOR launch over the same buffer (last-write-wins clobber, ADR 0020).
    private bool _editorOpen;

    /// <param name="app">The running application (for the default UI-thread marshal).</param>
    /// <param name="request">What to ask for (title, initial text, single- vs multi-line).</param>
    /// <param name="openInEditor">The Ctrl+E escape hatch: hands the current buffer to <c>$EDITOR</c>
    /// and returns the edited text, or <c>null</c> to leave the buffer unchanged.</param>
    /// <param name="resolve">Called with the buffer on submit, or <c>null</c> on cancel. This is the
    /// close seam — production captures the value and requests stop; a test records it.</param>
    /// <param name="ct">Flows into the Ctrl+E editor call.</param>
    /// <param name="post">Marshals the Ctrl+E result back onto the UI thread; defaults to
    /// <see cref="IApplication.Invoke(Action)"/>. Injectable so a test observes the refill synchronously.</param>
    public TextInputDialog(
        IApplication app,
        TextInputRequest request,
        Func<string, CancellationToken, Task<string?>> openInEditor,
        Action<string?> resolve,
        CancellationToken ct = default,
        Action<Action>? post = null)
    {
        _request = request;
        _openInEditor = openInEditor;
        _resolve = resolve;
        _dialogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _post = post ?? app.Invoke;
    }

    /// <summary>Test seam: the editable field, so a view-level test can drive/read it.</summary>
    internal View Field => _field ?? throw new InvalidOperationException("Build() first");

    /// <summary>Test seam: the current buffer text.</summary>
    internal string Text => _field?.Text ?? "";

    /// <summary>Test seam: the key hint / status line (carries a Ctrl+E launch-failure message).</summary>
    internal string Hint => _hint?.Text ?? "";

    /// <summary>
    /// Builds and wires the overlay (editable field, key hint, key handlers) without starting the
    /// run loop, so a view-level test can drive key delivery headlessly.
    /// </summary>
    internal Dialog Build()
    {
        var multiline = !_request.SingleLine;
        var dialog = new Dialog
        {
            Title = _request.Title,
            Width = Dim.Percent(multiline ? 70 : 60),
            Height = multiline ? Dim.Percent(60) : 7,
        };

        View field;
        if (multiline)
        {
            // TextView is obsolete in 2.4.16 (favoring the external Editor package); an editable
            // short-text pane doesn't justify that dependency — same call as TextDialog.
#pragma warning disable CS0618
            field = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1), // leave the last row for the hint
                ReadOnly = false,
                Text = _request.Initial,
            };
#pragma warning restore CS0618
        }
        else
        {
            field = new TextField
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Text = _request.Initial,
            };
        }
        _field = field;

        var hint = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = multiline
                ? "Enter submit · Ctrl+J newline · Ctrl+E $EDITOR · Esc cancel"
                : "Enter submit · Ctrl+E $EDITOR · Esc cancel",
        };
        _hint = hint;

        // The focused field eats keys first, so subscribe the handler there too (mirrors
        // TextDialog / ThreadViewDialog). Suppress the Dialog's default-accept so Enter is ours.
        field.KeyDown += HandleKey;
        dialog.KeyDown += HandleKey;
        dialog.Accepting += (_, e) => e.Handled = true;

        dialog.Add(field);
        dialog.Add(hint);
        return dialog;
    }

    private void HandleKey(object? sender, Key key)
    {
        if (_closed)
        {
            return; // already submitted/cancelled; ignore queued keys
        }

        var baseCode = key.NoShift.NoAlt.NoCtrl.KeyCode;

        if (baseCode == KeyCode.Esc)
        {
            key.Handled = true;
            Close(null);
            return;
        }

        if (key.IsCtrl && baseCode == KeyCode.E)
        {
            key.Handled = true;
            OpenInEditor();
            return;
        }

        // The newline chord (multi-line only): a Ctrl/Shift modifier on Enter — which is also how the
        // dotnet/ansi driver delivers a physical Ctrl+J (byte 0x0A), as Enter|CtrlMask (see remarks) —
        // plus a defensive KeyCode.J|CtrlMask clause for the Win32 `windows` driver. Plain, unmodified
        // Enter falls through to submit.
        if (!_request.SingleLine &&
            ((baseCode == KeyCode.Enter && (key.IsCtrl || key.IsShift)) ||
             (baseCode == KeyCode.J && key.IsCtrl)))
        {
            key.Handled = true;
            InsertNewline();
            return;
        }

        if (baseCode == KeyCode.Enter)
        {
            key.Handled = true;
            Close(Text);
            return;
        }

        // Any other key (printable runes, backspace, arrows) falls through to the field.
    }

    /// <summary>Resolve exactly once (submit or cancel); cancels any in-flight Ctrl+E editor call.</summary>
    private void Close(string? result)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _dialogCts.Cancel();
        _resolve(result);
    }

    private void InsertNewline()
    {
#pragma warning disable CS0618
        ((TextView)_field!).InsertText("\n");
#pragma warning restore CS0618
    }

    private void OpenInEditor()
    {
        if (_editorOpen)
        {
            return; // an $EDITOR session is already in flight; ignore the re-press
        }
        _editorOpen = true;
        var current = Text;
        _ = RunEditorAsync(current);
    }

    private async Task RunEditorAsync(string current)
    {
        string? edited;
        try
        {
            edited = await _openInEditor(current, _dialogCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed (or the user cancelled the editor) — nothing to surface.
            _post(() =>
            {
                _editorOpen = false;
                RefocusIfOpen();
            });
            return;
        }
        catch (Exception ex)
        {
            // Broad by design: this is a fire-and-forget UI task, so any launch/IO failure must be
            // caught (never left unobserved) AND surfaced — a silent no-op hatch violates ADR 0009.
            _post(() =>
            {
                _editorOpen = false;
                if (_closed)
                {
                    return;
                }
                if (_hint is not null)
                {
                    _hint.Text = $"couldn't open $EDITOR: {FirstLine(ex.Message)}";
                }
                _field?.SetFocus();
            });
            return;
        }

        // null → leave the buffer unchanged (do NOT submit); non-null → replace and return to field.
        _post(() =>
        {
            _editorOpen = false;
            if (_closed)
            {
                return; // dialog closed while the editor was open; discard and don't touch the field
            }
            if (edited is not null && _field is not null)
            {
                _field.Text = edited;
            }
            _field?.SetFocus();
        });
    }

    private void RefocusIfOpen()
    {
        if (!_closed)
        {
            _field?.SetFocus();
        }
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].TrimEnd('\r');
    }
}
