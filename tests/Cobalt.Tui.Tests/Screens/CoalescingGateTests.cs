using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The gate behind the diff dialog's stats-prefetch refresh. The prefetch raises one event per
/// file; the first queues a refresh and the rest ride along with it, rather than each queueing a
/// full chrome rebuild onto the UI thread while the reviewer scrolls.
/// </summary>
public class CoalescingGateTests
{
    [Fact]
    public void An_Event_Raised_While_The_Refresh_Runs_Queues_Another()
    {
        // The whole lifecycle in one pass: a burst collapses to one refresh, and the gate reopens
        // *before* that refresh does its work — so a file whose stats land mid-render still gets a
        // later refresh instead of being dropped. Reopening afterwards would swallow it; the last
        // assert is what tells those two apart.
        var gate = new CoalescingGate();
        Assert.True(gate.TryQueue());  // the first event queues a refresh
        Assert.False(gate.TryQueue()); // the burst behind it rides along

        var queuedFromInsideTheRefresh = false;
        gate.Run(() => queuedFromInsideTheRefresh = gate.TryQueue());

        Assert.True(queuedFromInsideTheRefresh);
    }
}
