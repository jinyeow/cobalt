using System.Text;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.App;

/// <summary>
/// Renders the two tab rows as text (ADR 0021): the section tabs, and the PR
/// sub-tab row with the active tab bracketed and its row count inline. Pure string
/// building — the views only assign the results. The PR row derives from the
/// view-model's cycle order so the strip can never advertise an order that no
/// longer matches what [ / ] actually walk.
/// </summary>
public static class TabStripFormatter
{
    /// <summary>
    /// The top-level section tabs, active one bracketed. Deliberately no jump-chord
    /// noise in the labels (UAT feedback) — g1/g2 are discoverable via `?` help.
    /// </summary>
    public static string Sections(AppSection active) =>
        " " + Tab("Work Items", active == AppSection.WorkItems)
        + " " + Tab("Pull Requests", active == AppSection.PullRequests);

    /// <summary>
    /// The PR sub-tab row in the view-model's cycle order; the active tab is bracketed
    /// and shows the loaded row count when known (counts for inactive tabs aren't fetched).
    /// </summary>
    public static string PrTabs(PrListFilter active, int? count)
    {
        var sb = new StringBuilder(" ");
        foreach (var tab in PrListViewModel.TabOrder)
        {
            if (sb.Length > 1)
            {
                sb.Append(" │ ");
            }
            var name = Name(tab);
            sb.Append(tab == active
                ? $"[{name}{(count is { } n ? $" {n}" : "")}]"
                : name);
        }
        return sb.ToString();
    }

    private static string Name(PrListFilter tab) => tab switch
    {
        PrListFilter.ReviewQueue => "review queue",
        PrListFilter.Team => "team",
        PrListFilter.Mine => "mine",
        _ => "active",
    };

    private static string Tab(string label, bool active) => active ? $"[{label}]" : $" {label} ";
}
