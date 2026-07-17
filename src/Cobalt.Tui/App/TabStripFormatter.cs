using System.Text;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.App;

/// <summary>
/// Renders the two tab rows as text (spec: lazygit-inspired redesign, stage A):
/// section tabs carrying their jump chords, and the PR sub-tab row with the active
/// tab bracketed and its row count inline. Pure string building — the views only
/// assign the results.
/// </summary>
public static class TabStripFormatter
{
    private static readonly (PrListFilter Tab, string Name)[] PrTabOrder =
    [
        (PrListFilter.ReviewQueue, "review queue"),
        (PrListFilter.Team, "team"),
        (PrListFilter.Mine, "mine"),
        (PrListFilter.Active, "active"),
    ];

    /// <summary>The top-level section tabs, active one bracketed, each with its jump chord.</summary>
    public static string Sections(AppSection active) =>
        " " + Tab("g1:Work Items", active == AppSection.WorkItems)
        + " " + Tab("g2:Pull Requests", active == AppSection.PullRequests);

    /// <summary>
    /// The PR sub-tab row in cycle order; the active tab is bracketed and shows the
    /// loaded row count when known (counts for inactive tabs aren't fetched).
    /// </summary>
    public static string PrTabs(PrListFilter active, int? count)
    {
        var sb = new StringBuilder(" ");
        foreach (var (tab, name) in PrTabOrder)
        {
            if (sb.Length > 1)
            {
                sb.Append(" │ ");
            }
            sb.Append(tab == active
                ? $"[{name}{(count is { } n ? $" {n}" : "")}]"
                : name);
        }
        return sb.ToString();
    }

    private static string Tab(string label, bool active) => active ? $"[{label}]" : $" {label} ";
}
