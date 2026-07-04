namespace Cobalt.Tui.Editor;

/// <summary>
/// The editor process could not be started (missing binary, bad
/// <c>$VISUAL</c>/<c>$EDITOR</c>). Carries a user-facing message the dialogs
/// surface on the message bar.
/// </summary>
public sealed class EditorLaunchException(string message, Exception? inner = null)
    : Exception(message, inner);
