using Cobalt.Tui.Input;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Maps the vim movement commands (with an optional count prefix) onto any focusable
/// scrollable <see cref="View"/> via <see cref="View.InvokeCommand(Command)"/> — the
/// one navigation API that behaves identically on <see cref="Terminal.Gui.Views.ListView"/>
/// (moves <c>SelectedItem</c>) and a read-only <see cref="Terminal.Gui.Views.TextView"/>
/// (moves <c>CurrentRow</c>). Ctrl-d/Ctrl-u are a true half page everywhere. The shell
/// router matches j/k/gg/G/Ctrl-d/u and marks the key handled, so movement must be
/// forwarded here or it is swallowed. Thin Terminal.Gui glue — PTY/headless-verified.
/// </summary>
public static class VimScroll
{
    public static bool Applies(AppCommand command) => command is
        AppCommand.MoveDown or AppCommand.MoveUp or
        AppCommand.MoveTop or AppCommand.MoveBottom or
        AppCommand.HalfPageDown or AppCommand.HalfPageUp;

    public static void Apply(View target, AppCommand command, int? count)
    {
        switch (command)
        {
            case AppCommand.MoveDown:
                Repeat(target, Command.Down, count ?? 1);
                break;
            case AppCommand.MoveUp:
                Repeat(target, Command.Up, count ?? 1);
                break;
            case AppCommand.MoveTop:
                // "gg" → top; "Ngg" → line N (1-based) = top then N-1 down.
                target.InvokeCommand(Command.Start);
                if (count is { } topLine)
                {
                    Repeat(target, Command.Down, topLine - 1);
                }
                break;
            case AppCommand.MoveBottom:
                if (count is { } bottomLine)
                {
                    // "NG" → line N (1-based), not the very bottom.
                    target.InvokeCommand(Command.Start);
                    Repeat(target, Command.Down, bottomLine - 1);
                }
                else
                {
                    target.InvokeCommand(Command.End);
                }
                break;
            case AppCommand.HalfPageDown:
                Repeat(target, Command.Down, HalfPage(target) * (count ?? 1));
                break;
            case AppCommand.HalfPageUp:
                Repeat(target, Command.Up, HalfPage(target) * (count ?? 1));
                break;
            default:
                break;
        }
    }

    private static int HalfPage(View target) => Math.Max(1, target.Viewport.Height / 2);

    private static void Repeat(View target, Command command, int times)
    {
        for (var i = 0; i < times; i++)
        {
            target.InvokeCommand(command);
        }
    }
}
