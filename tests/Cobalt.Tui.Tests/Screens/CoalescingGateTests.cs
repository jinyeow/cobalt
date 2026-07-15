using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The gate behind the diff dialog's stats-prefetch refresh. The prefetch raises one event per
/// file, each of which would otherwise queue a full chrome refresh onto the UI thread while the
/// reviewer is scrolling. Tested as pure logic rather than through the dialog because a headless
/// Application never drains Invoke, so the queued work would never run to observe.
/// </summary>
public class CoalescingGateTests
{
    [Fact]
    public void The_First_Event_Queues_A_Refresh()
    {
        Assert.True(new CoalescingGate().TryQueue());
    }

    [Fact]
    public void A_Burst_Of_Events_Queues_Only_One_Refresh()
    {
        var gate = new CoalescingGate();

        var queued = Enumerable.Range(0, 50).Count(_ => gate.TryQueue());

        Assert.Equal(1, queued);
    }

    [Fact]
    public void Concurrent_Events_Queue_Only_One_Refresh()
    {
        // The prefetch raises its event from threadpool continuations, so the racing callers
        // must agree on exactly one winner.
        var gate = new CoalescingGate();
        var queued = 0;

        Parallel.For(0, 500, _ =>
        {
            if (gate.TryQueue())
            {
                Interlocked.Increment(ref queued);
            }
        });

        Assert.Equal(1, queued);
    }

    [Fact]
    public void An_Event_Arriving_After_The_Refresh_Started_Queues_A_Fresh_One()
    {
        // Release runs before the refresh does its work, so an event raised by the still-running
        // prefetch during that refresh is not swallowed — its data gets a later refresh.
        var gate = new CoalescingGate();
        gate.TryQueue();

        gate.Release();

        Assert.True(gate.TryQueue());
        Assert.False(gate.TryQueue());
    }
}
