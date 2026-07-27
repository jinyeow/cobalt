using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Test double for <see cref="IUiPost"/>: records every posted action instead of running it,
/// mirroring how a headless (un-Init'd) <c>Application</c>'s <c>Invoke</c> never drains. Left
/// undrained, it reproduces today's "Invoke never fires" semantics for the list/dialog views;
/// <see cref="RunAll"/> lets a test deterministically drain the queue in FIFO order to observe
/// what a coalesced render would have painted.
/// </summary>
internal sealed class RecordingUiPost : IUiPost
{
    private readonly Lock _gate = new();

    public List<Action> Posted { get; } = [];

    /// <summary>Production posts arrive on threadpool continuations (a landed comment count, a
    /// finished detail load), so the queue is guarded — an unsynchronised <see cref="List{T}"/>
    /// mutated by a background <see cref="Post"/> while the test thread drains can lose items or
    /// throw out of <see cref="RunAll"/>.</summary>
    public void Post(Action action)
    {
        lock (_gate)
        {
            Posted.Add(action);
        }
    }

    /// <summary>Runs every queued action in FIFO order, including any it queues while draining.</summary>
    public void RunAll()
    {
        while (true)
        {
            Action action;
            lock (_gate)
            {
                if (Posted.Count == 0)
                {
                    return;
                }
                action = Posted[0];
                Posted.RemoveAt(0);
            }
            // Invoked outside the lock: a drained action may post again, and a background Post
            // must never block behind a running action.
            action();
        }
    }
}
