using Cobalt.Core.Models;
using Cobalt.Tui.App;
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
    public void Sections_Bracket_The_Active_One_Without_Chord_Noise()
    {
        // Jump chords (g1/g2) live in `?` help and the keybar, not the tab labels.
        var strip = TabStripFormatter.Sections(AppSection.WorkItems);

        Assert.Contains("[Work Items]", strip);
        Assert.Contains("Pull Requests", strip);
        Assert.DoesNotContain("[Pull Requests]", strip);
        Assert.DoesNotContain("g1", strip);
        Assert.DoesNotContain("g2", strip);
    }

    [Fact]
    public void Sections_Move_The_Brackets_With_The_Active_Section()
    {
        var strip = TabStripFormatter.Sections(AppSection.PullRequests);

        Assert.Contains("[Pull Requests]", strip);
        Assert.DoesNotContain("[Work Items]", strip);
    }

    [Fact]
    public void PrTabs_Highlight_The_Active_Tab_With_Its_Count()
    {
        var row = TabStripFormatter.PrTabs(PrListFilter.Team, 7);

        Assert.Contains("[team 7]", row);
        Assert.Contains("mine", row);
        Assert.Contains("active", row);
        // Only the active tab is bracketed.
        Assert.DoesNotContain("[mine", row);
    }

    [Fact]
    public void PrTabs_Without_A_Count_Still_Highlight_The_Active_Tab()
    {
        var row = TabStripFormatter.PrTabs(PrListFilter.Team, null);

        Assert.Contains("[team]", row);
    }

    [Fact]
    public void PrTabs_Render_In_The_Cycle_Order_Without_ReviewQueue()
    {
        // The personal review queue is out of the cycle (review-via-team orgs
        // always see it empty); Team leads as the default tab.
        var row = TabStripFormatter.PrTabs(PrListFilter.Active, 0);

        Assert.DoesNotContain("review queue", row);
        var team = row.IndexOf("team", StringComparison.Ordinal);
        var mine = row.IndexOf("mine", StringComparison.Ordinal);
        var active = row.IndexOf("[active 0]", StringComparison.Ordinal);
        Assert.True(team >= 0 && team < mine && mine < active);
    }
}
