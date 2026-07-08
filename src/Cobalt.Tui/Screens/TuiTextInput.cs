using Cobalt.Tui.Editor;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Production <see cref="ITextInput"/> (ADR 0020): shows the in-TUI <see cref="TextInputDialog"/>
/// and returns the entered text, or <c>null</c> when the user cancels. Flows call
/// <see cref="ReadAsync"/> from background tasks with <c>ConfigureAwait(false)</c>; it marshals the
/// build + modal <c>app.Run</c> onto the UI thread via <see cref="IApplication.Invoke(Action)"/>
/// (a nested run loop pumps from an <c>Invoke</c> callback — the same primitive
/// <see cref="MessageBox"/> relies on) and completes a <see cref="TaskCompletionSource{TResult}"/>.
/// The Ctrl+E escape hatch hands the current buffer to <c>$EDITOR</c> via
/// <see cref="EditorService.EditAsync"/>.
/// </summary>
public sealed class TuiTextInput(IApplication app, EditorService editor) : ITextInput
{
    public Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default)
    {
        // RunContinuationsAsynchronously keeps the caller's continuation off the UI thread.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            string? result = null;
            try
            {
                Dialog? dialog = null;
                var view = new TextInputDialog(
                    app,
                    request,
                    (buffer, ct) => editor.EditAsync(buffer, ".md", ct),
                    submitted =>
                    {
                        result = submitted;
                        if (dialog is { } d)
                        {
                            app.RequestStop(d);
                        }
                    },
                    cancellationToken);
                dialog = view.Build();
                using (dialog)
                {
                    app.Run(dialog);
                }
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
