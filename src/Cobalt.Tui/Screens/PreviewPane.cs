using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The workspace's read-only preview pane (ADR 0024): it displays the selected item's
/// detail as composed by the shared formatters and does nothing else — scroll is its only
/// verb, every action stays modal. Thin Terminal.Gui glue; the text it shows and the pane's
/// visibility are decided outside it.
/// </summary>
#pragma warning disable CS0618 // read-only scrollable pane; see WorkItemDetailDialog
public sealed class PreviewPane : View
{
    private readonly TextView _body;

    public PreviewPane()
    {
        CanFocus = true;
        // WordWrap stays OFF (unlike the detail dialogs): the Summary tier is already
        // width-clamped by the formatter, so one logical line is exactly one row — which is
        // what keeps the line cap an honest count of rows.
        _body = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = true, // position indicator; content is scrolled pager-style (VimScroll)
            // The pane itself takes focus, not the TextView: a focused ReadOnly TextView
            // swallows every printable rune before the shell's Window-level KeyDown runs (the
            // trap PrDetailDialog works around by subscribing its own handler). Here the shell
            // owns all routing (ADR 0024), so the keys must reach it.
            CanFocus = false,
        };
        Add(_body);
        SetContent(""); // paint the empty state up front
    }

    /// <summary>Test seam: the read-only scroll pane, exposed so a view-level test can assert on it.</summary>
    internal TextView Body => _body;

    /// <summary>Shows <paramref name="text"/> — the shared formatters' Summary-tier output —
    /// capped to <see cref="PreviewBudget.MaxLines"/>. Content taller than the pane is kept, not
    /// truncated: the overflow is what the pane scrolls through.</summary>
    public void SetContent(string text) => _body.Text = text.Length == 0
        ? EmptyState
        : PreviewBudget.Fit(text, PreviewBudget.MaxLines);

    /// <summary>Shown while the pane has nothing to display, so it never reads as a blank hole.</summary>
    private const string EmptyState = "(no preview)";

    /// <summary>
    /// Scrolls the pane for a matched vim movement command — the pane's only verb (ADR 0024).
    /// Routed here by the shell (which owns workspace focus), through the shared
    /// <see cref="VimScroll"/> seam rather than a second scroll implementation.
    /// </summary>
    public void Scroll(AppCommand command, int? count) => VimScroll.Apply(_body, command, count);
}
#pragma warning restore CS0618
