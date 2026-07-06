using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// A scrollable modal text pane (help, messages, per-dialog key reference).
/// MessageBox chokes on content taller than the screen, so this uses a read-only
/// <see cref="TextView"/> with q/Esc/Enter to close. Movement keys route through the
/// shared <see cref="KeymapRouter"/>/<see cref="VimScroll"/> seam, so j/k/gg/G/Ctrl-d/u
/// scroll the pane (a ReadOnly TextView otherwise swallows those runes). One seam
/// shared by the shell and every detail dialog.
/// </summary>
internal static class TextDialog
{
    public static void Show(IApplication app, string title, string text)
    {
        using var dialog = Build(app, title, text, out _);
        app.Run(dialog);
    }

    // TextView is marked obsolete in favor of the external tui-cs/Editor package; a
    // read-only scrollable pane doesn't justify that dependency.
#pragma warning disable CS0618
    /// <summary>
    /// Builds and wires the overlay without starting the run loop, exposing the inner
    /// <see cref="TextView"/> so a headless view-level test can drive scrolling.
    /// </summary>
    internal static Dialog Build(IApplication app, string title, string text, out TextView view)
    {
        var router = new KeymapRouter(KeyBindingTable.Default());
        var dialog = new Dialog
        {
            Title = $"{title} — q to close",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };
        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = text,
        };
        view = textView;

        // The focused ReadOnly TextView swallows the movement runes and q/Enter before
        // dialog.KeyDown runs, so subscribe the handler to the TextView too. Movement is
        // forwarded to VimScroll; q/Esc/Enter close.
        void OnKey(object? sender, Terminal.Gui.Input.Key key)
        {
            var token = KeyTokenizer.ToToken(key);
            if (token is null)
            {
                return;
            }
            var result = router.Feed(token, KeyScope.Global);
            if (result.Kind == KeyResultKind.Matched && VimScroll.Applies(result.Command))
            {
                VimScroll.Apply(textView, result.Command, result.Count);
                key.Handled = true;
                return;
            }
            if (result.Kind == KeyResultKind.Pending)
            {
                key.Handled = true; // swallow an in-progress sequence (e.g. after 'g')
                return;
            }
            // q (Back) / Enter (Open) / Esc all close the overlay.
            if (token is "q" or "Esc" or "Enter")
            {
                key.Handled = true;
                app.RequestStop(dialog);
            }
        }
        textView.KeyDown += OnKey;
        dialog.KeyDown += OnKey;
        dialog.Add(textView);
        return dialog;
    }
#pragma warning restore CS0618
}
