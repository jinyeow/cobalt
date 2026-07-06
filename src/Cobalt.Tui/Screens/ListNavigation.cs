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
        // extend: false — plain cursor movement. `extend: true` extends a marked
        // selection (only visible with ShowMarks/MarkMultiple), which cobalt never
        // wants; passing true was a latent bug if marking is ever enabled.
        switch (command)
        {
            case AppCommand.MoveDown:
                list.MoveDown(false);
                break;
            case AppCommand.MoveUp:
                list.MoveUp(false);
                break;
            case AppCommand.MoveTop:
                list.MoveHome(false);
                break;
            case AppCommand.MoveBottom:
                list.MoveEnd(false);
                break;
            case AppCommand.HalfPageDown:
                list.MovePageDown(false);
                break;
            case AppCommand.HalfPageUp:
                list.MovePageUp(false);
                break;
            default:
                break;
        }
    }
}
