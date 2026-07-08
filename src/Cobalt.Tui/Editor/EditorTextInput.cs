namespace Cobalt.Tui.Editor;

/// <summary>
/// <see cref="ITextInput"/> backed by the <c>$EDITOR</c> handoff (<see cref="EditorService"/>).
/// Preserves the original behaviour for any call site not yet wired to the in-TUI editor, and
/// is the target of the in-TUI editor's Ctrl-E escape hatch. <see cref="TextInputRequest.SingleLine"/>
/// has no meaning for an external editor and is ignored.
/// </summary>
public sealed class EditorTextInput(EditorService editor) : ITextInput
{
    public Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default) =>
        editor.EditAsync(request.Initial, ".md", cancellationToken);
}
