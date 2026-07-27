namespace Cobalt.Tui.Tasks;

/// <summary>
/// Collapses a burst of events into a single queued refresh: the first caller to
/// <see cref="TryQueue"/> owns the refresh, every caller after it is told one is already coming,
/// and <see cref="Run"/> reopens the gate before running it. Interlocked because the raising
/// thread and the running (UI) thread are not the same.
/// </summary>
internal sealed class CoalescingGate
{
    private int _queued;

    /// <summary>True if the caller should queue the refresh; false if one is already queued.</summary>
    public bool TryQueue() => Interlocked.CompareExchange(ref _queued, 1, 0) == 0;

    /// <summary>
    /// Runs the queued refresh, reopening the gate first so an event raised while
    /// <paramref name="refresh"/> is running queues a new one instead of being dropped. The order
    /// lives here rather than at the call site because it is the whole point of the gate and
    /// nothing at the call site would reveal it being wrong.
    /// </summary>
    public void Run(Action refresh)
    {
        Interlocked.Exchange(ref _queued, 0);
        refresh();
    }
}
