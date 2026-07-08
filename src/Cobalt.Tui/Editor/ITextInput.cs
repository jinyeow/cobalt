namespace Cobalt.Tui.Editor;

/// <summary>
/// Requests a short piece of text from the user for comments, replies, and small prompts.
/// The in-TUI implementation (<c>TuiTextInput</c>) shows an editable field with no terminal
/// handoff — instant, and headless-testable, unlike the <c>$EDITOR</c> suspend/resume path
/// (ADR 0009). <see cref="EditorTextInput"/> is the <c>$EDITOR</c>-backed fallback and the
/// target of the in-TUI editor's escape hatch. Returns <c>null</c> when the user cancels.
/// </summary>
public interface ITextInput
{
    Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What to ask for.</summary>
/// <param name="Title">The field's heading/prompt.</param>
/// <param name="Initial">Prefilled text (empty for a fresh comment).</param>
/// <param name="SingleLine">
/// A one-line field where Enter submits and there is no newline — for short values like a
/// thread id or an assignee. Multi-line (the default) submits on Enter with a chord for newline.
/// </param>
public sealed record TextInputRequest(string Title, string Initial = "", bool SingleLine = false);
