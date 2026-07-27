namespace Cobalt.Tui.Input;

/// <summary>Command classification shared by the routing layer and the Terminal.Gui glue.</summary>
public static class AppCommandExtensions
{
    /// <summary>
    /// Whether the command is a vim movement (j/k/gg/G/Ctrl-d/Ctrl-u) — the set that scrolls or
    /// moves a cursor rather than acting on the selection. Lives here rather than in
    /// <c>Screens/VimScroll</c> (which still exposes it as <c>VimScroll.Applies</c>) so the UI-free
    /// routing layer can ask the question without reaching into a Terminal.Gui type.
    /// </summary>
    public static bool IsMovement(this AppCommand command) => command is
        AppCommand.MoveDown or AppCommand.MoveUp or
        AppCommand.MoveTop or AppCommand.MoveBottom or
        AppCommand.HalfPageDown or AppCommand.HalfPageUp;
}
