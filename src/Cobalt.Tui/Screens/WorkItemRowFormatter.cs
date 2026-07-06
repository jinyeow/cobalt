using Cobalt.Core.Models;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Pure, width-aware row formatting for the work-item list: fixed id/type/state
/// columns, then the title takes the slack up to the trailing iteration and
/// changed-date columns. Every row is exactly <c>width</c> cells.
/// </summary>
internal static class WorkItemRowFormatter
{
    private const int IdWidth = 6;
    private const int TypeWidth = 8;
    private const int StateWidth = 10;
    private const int IterationWidth = 16;
    private const int DateWidth = 10; // yyyy-MM-dd

    public static string Format(WorkItem item, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        var id = item.Id.ToString().PadLeft(IdWidth);
        var type = RowText.Fit(item.WorkItemType, TypeWidth);
        var state = RowText.Fit(item.State, StateWidth);
        var iteration = RowText.Fit(LastSegment(item.IterationPath), IterationWidth);
        var date = (item.ChangedDate?.ToString("yyyy-MM-dd") ?? "").PadRight(DateWidth);

        // Fixed cells plus their two-space separators; the title fills what's left.
        var fixedLen = IdWidth + 2 + TypeWidth + 2 + StateWidth + 2 + 2 + IterationWidth + 2 + DateWidth;
        var titleSpace = Math.Max(0, width - fixedLen);
        var title = RowText.TitleCell(item.Title, titleSpace);

        return RowText.Clamp($"{id}  {type}  {state}  {title}  {iteration}  {date}", width);
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
