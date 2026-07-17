using System.Text;
using Cobalt.Core.Models;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.App;

/// <summary>
/// Renders the two tab rows as text (ADR 0021):
/// section tabs carrying their jump chords, and the PR sub-tab row with the active
/// tab bracketed and its row count inline. Pure string building — the views only
/// assign the results. Both rows derive from the behavioural sources of truth
/// (the binding table for chords, the view-model's cycle order for tabs) so the
/// strip can never advertise keys or an order that no longer match behaviour.
/// </summary>
public static class TabStripFormatter
{
    /// <summary>The top-level section tabs, active one bracketed, each with its live jump chord.</summary>
    public static string Sections(AppSection active, KeyBindingTable table) =>
        " " + Tab(Label(table, AppCommand.SectionWorkItems, "Work Items"), active == AppSection.WorkItems)
        + " " + Tab(Label(table, AppCommand.SectionPullRequests, "Pull Requests"), active == AppSection.PullRequests);

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

    /// <summary>The section label prefixed with its jump chord read from the live table.</summary>
    private static string Label(KeyBindingTable table, AppCommand command, string name)
    {
        foreach (var (sequence, bound) in table.Visible(KeyScope.Global))
        {
            if (bound == command)
            {
                return $"{string.Join("", sequence)}:{name}";
            }
        }
        return name; // unbound: show the plain name rather than a dead chord
    }

    private static string Tab(string label, bool active) => active ? $"[{label}]" : $" {label} ";
}
