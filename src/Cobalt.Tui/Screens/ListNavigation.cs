using Cobalt.Tui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Maps the vim movement commands to a Terminal.Gui <see cref="ListView"/>'s built-in
/// navigation. The shell router matches j/k/gg/G/Ctrl-d/u and marks the key handled,
/// so movement must be forwarded here or it is swallowed. Ctrl-d/u map to full-page
/// moves (ListView has no half-page primitive). Thin Terminal.Gui glue — PTY-verified.
/// </summary>
public static class ListNavigation
{
    public static bool Applies(AppCommand command) => command is
        AppCommand.MoveDown or AppCommand.MoveUp or
        AppCommand.MoveTop or AppCommand.MoveBottom or
        AppCommand.HalfPageDown or AppCommand.HalfPageUp;

    public static void Apply(ListView list, AppCommand command)
    {
        switch (command)
        {
            case AppCommand.MoveDown:
                list.MoveDown(true);
                break;
            case AppCommand.MoveUp:
                list.MoveUp(true);
                break;
            case AppCommand.MoveTop:
                list.MoveHome(true);
                break;
            case AppCommand.MoveBottom:
                list.MoveEnd(true);
                break;
            case AppCommand.HalfPageDown:
                list.MovePageDown(true);
                break;
            case AppCommand.HalfPageUp:
                list.MovePageUp(true);
                break;
            default:
                break;
        }
    }
}
