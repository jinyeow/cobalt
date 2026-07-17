using Cobalt.Core.Models;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The real tab strip (ADR 0021): section tabs
/// carry their jump chords, and the PR sub-tabs render as a visible tab row with
/// the active tab highlighted and its row count shown.
/// </summary>
public class TabStripFormatterTests
{
    [Fact]
    public void Sections_Show_Jump_Chords_And_Bracket_The_Active_One()
    {
        var strip = TabStripFormatter.Sections(AppSection.WorkItems, KeyBindingTable.Default());

        Assert.Contains("[g1:Work Items]", strip);
        Assert.Contains("g2:Pull Requests", strip);
        Assert.DoesNotContain("[g2:Pull Requests]", strip);
    }

    [Fact]
    public void Sections_Move_The_Brackets_With_The_Active_Section()
    {
        var strip = TabStripFormatter.Sections(AppSection.PullRequests, KeyBindingTable.Default());

        Assert.Contains("[g2:Pull Requests]", strip);
        Assert.DoesNotContain("[g1:Work Items]", strip);
    }

    [Fact]
    public void Sections_Read_The_Jump_Chords_From_The_Live_Table()
    {
        // A rebound section jump must change the advertised chord — the strip is
        // generated from the table, never a hardcoded label.
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "g w", AppCommand.SectionWorkItems);

        var strip = TabStripFormatter.Sections(AppSection.WorkItems, table);

        Assert.Contains("[gw:Work Items]", strip);
        Assert.Contains("Pull Requests", strip); // unbound: plain name, no dead chord
        Assert.DoesNotContain("g2:Pull Requests", strip);
    }

    [Fact]
    public void PrTabs_Highlight_The_Active_Tab_With_Its_Count()
    {
        var row = TabStripFormatter.PrTabs(PrListFilter.Team, 7);

        Assert.Contains("[team 7]", row);
        Assert.Contains("review queue", row);
        Assert.Contains("mine", row);
        Assert.Contains("active", row);
        // Only the active tab is bracketed.
        Assert.DoesNotContain("[mine", row);
    }

    [Fact]
    public void PrTabs_Without_A_Count_Still_Highlight_The_Active_Tab()
    {
        var row = TabStripFormatter.PrTabs(PrListFilter.ReviewQueue, null);

        Assert.Contains("[review queue]", row);
    }

    [Fact]
    public void PrTabs_Render_In_The_Cycle_Order()
    {
        var row = TabStripFormatter.PrTabs(PrListFilter.Active, 0);

        var queue = row.IndexOf("review queue", StringComparison.Ordinal);
        var team = row.IndexOf("team", StringComparison.Ordinal);
        var mine = row.IndexOf("mine", StringComparison.Ordinal);
        var active = row.IndexOf("[active 0]", StringComparison.Ordinal);
        Assert.True(queue < team && team < mine && mine < active);
    }
}
