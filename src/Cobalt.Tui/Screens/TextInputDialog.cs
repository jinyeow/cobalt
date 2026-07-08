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
/// Newline-chord probe (Terminal.Gui 2.4.16): Ctrl+Enter / Shift+Enter cannot be represented
/// distinctly on the wire — there is no CSI-u / kitty keyboard protocol in the package, and the
/// ANSI encoder documents that Ctrl+modifier combinations on Enter collapse to the same code as
/// plain Enter. The fallback is <b>Ctrl+J</b> (literal line-feed, always delivered). A decoder
/// probe of <c>AnsiKeyConverter.ToKey</c> shows the dotnet/ansi driver maps a physical Ctrl+J
/// (byte 0x0A) to <c>KeyCode.Enter | CtrlMask</c> (base <see cref="KeyCode.Enter"/>, Ctrl set) —
/// NOT <c>KeyCode.J | CtrlMask</c> — while plain Enter (CR, 0x0D) is <see cref="KeyCode.Enter"/>
/// with no modifier. So the newline chord is detected as "Enter with a Ctrl/Shift modifier"
/// (covering Ctrl+J and any terminal that does deliver Ctrl/Shift+Enter distinctly), plus a
/// defensive <c>KeyCode.J | CtrlMask</c> clause for the Win32 <c>windows</c> driver. Plain,
/// unmodified Enter submits.
/// </remarks>
internal sealed class TextInputDialog
{
    private readonly IApplication _app;
    private readonly TextInputRequest _request;
    private readonly Func<string, CancellationToken, Task<string?>> _openInEditor;
    private readonly Action<string?> _resolve;
    private readonly Action<Action> _post;
    private readonly CancellationToken _ct;
    private View? _field;

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
        _app = app;
        _request = request;
        _openInEditor = openInEditor;
        _resolve = resolve;
        _ct = ct;
        _post = post ?? app.Invoke;
    }

    /// <summary>Test seam: the editable field, so a view-level test can drive/read it.</summary>
    internal View Field => _field ?? throw new InvalidOperationException("Build() first");

    /// <summary>Test seam: the current buffer text.</summary>
    internal string Text => _field?.Text ?? "";

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
        var baseCode = key.NoShift.NoAlt.NoCtrl.KeyCode;

        if (baseCode == KeyCode.Esc)
        {
            key.Handled = true;
            _resolve(null);
            return;
        }

        if (key.IsCtrl && baseCode == KeyCode.E)
        {
            key.Handled = true;
            OpenInEditor();
            return;
        }

        if (baseCode == KeyCode.Enter)
        {
            // Ctrl+J arrives here as Enter|CtrlMask under the dotnet/ansi driver (see remarks);
            // a Ctrl/Shift modifier means "newline", plain Enter means "submit".
            if (!_request.SingleLine && (key.IsCtrl || key.IsShift))
            {
                key.Handled = true;
                InsertNewline();
                return;
            }
            key.Handled = true;
            _resolve(Text);
            return;
        }

        // Defensive: the Win32 `windows` driver may deliver a physical Ctrl+J as KeyCode.J|CtrlMask.
        if (!_request.SingleLine && key.IsCtrl && baseCode == KeyCode.J)
        {
            key.Handled = true;
            InsertNewline();
            return;
        }

        // Any other key (printable runes, backspace, arrows) falls through to the field.
    }

    private void InsertNewline()
    {
#pragma warning disable CS0618
        ((TextView)_field!).InsertText("\n");
#pragma warning restore CS0618
    }

    private void OpenInEditor()
    {
        var current = Text;
        _ = RunEditorAsync(current);
    }

    private async Task RunEditorAsync(string current)
    {
        string? edited;
        try
        {
            edited = await _openInEditor(current, _ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or IOException or OperationCanceledException)
        {
            // The $EDITOR hatch failed/was cancelled; keep the buffer and return to the field.
            _post(() => _field?.SetFocus());
            return;
        }

        // null → leave the buffer unchanged (do NOT submit); non-null → replace and return to field.
        _post(() =>
        {
            if (edited is not null && _field is not null)
            {
                _field.Text = edited;
            }
            _field?.SetFocus();
        });
    }
}
