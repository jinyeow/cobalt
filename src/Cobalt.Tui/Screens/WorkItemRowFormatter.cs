using Cobalt.Core.Models;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Derived columns for a work-item result set: the project column only appears when the
/// rows actually span more than one project (the org-scoped, cross-project case), mirroring
/// <see cref="PrColumns"/> so cross-project items are distinguishable.
/// </summary>
internal readonly record struct WorkItemColumns(bool ShowProject, int ProjectWidth)
{
    public const int MaxProjectWidth = 18;

    public static WorkItemColumns For(IReadOnlyList<WorkItem> rows)
    {
        var projects = rows.Select(r => r.TeamProject).Where(p => p.Length > 0).Distinct().ToList();
        var showProject = projects.Count > 1;
        var projectWidth = showProject ? Math.Min(MaxProjectWidth, projects.Max(p => p.Length)) : 0;
        return new WorkItemColumns(showProject, projectWidth);
    }
}

/// <summary>
/// Pure, width-aware row formatting for the work-item list: fixed id/type/state
/// (and an optional project) columns, then the title takes the slack up to the
/// trailing iteration and changed-date columns. Every row is exactly <c>width</c> cells.
/// </summary>
internal static class WorkItemRowFormatter
{
    private const int IdWidth = 6;
    private const int TypeWidth = 8;
    private const int StateWidth = 10;
    private const int IterationWidth = 16;
    private const int DateWidth = 10; // yyyy-MM-dd

    public static string Format(WorkItem item, int width, WorkItemColumns cols)
    {
        if (width <= 0)
        {
            return "";
        }

        var id = item.Id.ToString().PadLeft(IdWidth);
        var type = RowText.Fit(item.WorkItemType, TypeWidth);
        var state = RowText.Fit(item.State, StateWidth);
        var project = cols.ShowProject ? RowText.Fit(item.TeamProject, cols.ProjectWidth) + "  " : "";
        var iteration = RowText.Fit(LastSegment(item.IterationPath), IterationWidth);
        var date = (item.ChangedDate?.ToString("yyyy-MM-dd") ?? "").PadRight(DateWidth);

        // Fixed cells plus their two-space separators; the title fills what's left.
        var projectLen = cols.ShowProject ? cols.ProjectWidth + 2 : 0;
        var fixedLen = IdWidth + 2 + TypeWidth + 2 + StateWidth + 2 + projectLen + 2 + IterationWidth + 2 + DateWidth;
        var titleSpace = Math.Max(0, width - fixedLen);
        var title = RowText.TitleCell(item.Title, titleSpace);

        return RowText.Clamp($"{id}  {type}  {state}  {project}{title}  {iteration}  {date}", width);
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
