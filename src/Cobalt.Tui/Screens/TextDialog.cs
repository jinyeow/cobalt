using Cobalt.Tui.App;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// A scrollable modal text pane (help, messages, per-dialog key reference).
/// MessageBox chokes on content taller than the screen, so this uses a read-only
/// <see cref="TextView"/> with q/Esc/Enter to close and native j/k/arrows/PageUp/
/// PageDown scrolling. One seam shared by the shell and every detail dialog.
/// </summary>
internal static class TextDialog
{
    public static void Show(IApplication app, string title, string text)
    {
        using var dialog = new Dialog
        {
            Title = $"{title} — q to close",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };
        // TextView is marked obsolete in favor of the external tui-cs/Editor package;
        // a read-only scrollable pane doesn't justify that dependency.
#pragma warning disable CS0618
        var view = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = text,
        };
#pragma warning restore CS0618

        // The focused ReadOnly TextView swallows q/Enter before dialog.KeyDown runs,
        // so subscribe the close handler to the TextView too; other keys (arrows,
        // PageUp/PageDown) fall through to its native scrolling.
        void OnKey(object? sender, Terminal.Gui.Input.Key key)
        {
            var token = KeyTokenizer.ToToken(key);
            if (token is "q" or "Esc" or "Enter")
            {
                key.Handled = true;
                app.RequestStop(dialog);
            }
        }
        view.KeyDown += OnKey;
        dialog.KeyDown += OnKey;
        dialog.Add(view);
        app.Run(dialog);
    }
}
