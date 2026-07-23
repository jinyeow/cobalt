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
    public List<Action> Posted { get; } = [];

    public void Post(Action action) => Posted.Add(action);

    /// <summary>Runs every queued action in FIFO order, including any it queues while draining.</summary>
    public void RunAll()
    {
        while (Posted.Count > 0)
        {
            var action = Posted[0];
            Posted.RemoveAt(0);
            action();
        }
    }
}
