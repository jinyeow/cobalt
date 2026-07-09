using System.Drawing;
using Cobalt.Tui.Input;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Maps the vim movement commands (with an optional count prefix) onto a focusable
/// scrollable <see cref="View"/>. A <see cref="Terminal.Gui.Views.ListView"/> scrolls via
/// <see cref="View.InvokeCommand(Command)"/> (moving its visible <c>SelectedItem</c> bar);
/// a read-only <see cref="Terminal.Gui.Views.TextView"/> is scrolled like a pager instead
/// (see <c>ScrollTextView</c>) because its caret is invisible and only scrolls the viewport
/// after reaching the edge. Ctrl-d/Ctrl-u are a true half page everywhere. The shell router
/// matches j/k/gg/G/Ctrl-d/u and marks the key handled, so movement must be forwarded here
/// or it is swallowed. Thin Terminal.Gui glue — PTY/headless-verified.
/// </summary>
#pragma warning disable CS0618 // read-only scrollable TextView pane; see WorkItemDetailDialog
public static class VimScroll
{
    public static bool Applies(AppCommand command) => command is
        AppCommand.MoveDown or AppCommand.MoveUp or
        AppCommand.MoveTop or AppCommand.MoveBottom or
        AppCommand.HalfPageDown or AppCommand.HalfPageUp;

    public static void Apply(View target, AppCommand command, int? count)
    {
        // A ReadOnly TextView moves an invisible caret that only scrolls the viewport
        // once it reaches the bottom edge, so the first key presses look inert. Scroll it
        // like a pager instead: pin the caret to the top visible row and ScrollTo it, so
        // every key advances the view a line/page immediately. (Terminal.Gui exposes no
        // scroll-offset getter; the invisible caret's CurrentRow doubles as the tracker.)
        if (target is TextView textView)
        {
            ScrollTextView(textView, command, count);
            return;
        }

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

    private static void ScrollTextView(TextView view, AppCommand command, int? count)
    {
        var height = Math.Max(1, view.Viewport.Height);
        var maxTop = Math.Max(0, view.Lines - height);
        var half = Math.Max(1, height / 2);
        var top = view.CurrentRow;
        var target = command switch
        {
            AppCommand.MoveDown => top + (count ?? 1),
            AppCommand.MoveUp => top - (count ?? 1),
            AppCommand.HalfPageDown => top + (half * (count ?? 1)),
            AppCommand.HalfPageUp => top - (half * (count ?? 1)),
            // "gg" → top; "Ngg" → line N (1-based). "G" → last page; "NG" → line N.
            AppCommand.MoveTop => (count ?? 1) - 1,
            AppCommand.MoveBottom => count is { } line ? line - 1 : maxTop,
            _ => top,
        };
        target = Math.Clamp(target, 0, maxTop);
        view.InsertionPoint = new Point(0, target);
        view.ScrollTo(new Point(0, target));
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
#pragma warning restore CS0618
