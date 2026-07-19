using Cobalt.Core.Ado;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The <c>:log</c> overlay: the recent ADO operations (name, masked route shape, duration,
/// outcome), newest last, in the same scrollable read-only pane as help/messages
/// (<see cref="TextDialog"/>) — <c>q</c>/<c>Esc</c> to close. The source is
/// <see cref="OperationLog.History"/>, fed by <c>AdoHttp.OperationObserver</c>; it can never
/// carry a token because <see cref="AdoOperation"/> only exposes the redacted route shape.
/// </summary>
internal static class OperationLogDialog
{
    public static void Show(IApplication app, OperationLog operations) =>
        TextDialog.Show(app, "log", Format(operations.History));

    /// <summary>Renders the history newest-last; pure so the formatting is unit-testable without a run loop.</summary>
    internal static string Format(IReadOnlyList<AdoOperation> history) =>
        history.Count == 0
            ? "no ADO requests yet"
            : string.Join("\n", history.Select(FormatLine));

    private static string FormatLine(AdoOperation op)
    {
        var outcome = op.Status is { } status ? status.ToString() : "—";
        var duration = $"{op.Duration.TotalMilliseconds:F0}ms";
        return $"{op.At:HH:mm:ss} {op.Name,-6} {op.RouteShape}  {duration}  {outcome}";
    }
}
