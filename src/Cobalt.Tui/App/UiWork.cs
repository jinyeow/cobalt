namespace Cobalt.Tui.App;

/// <summary>
/// Runs a synchronous, UI-affine unit of work (e.g. a <c>MessageBox</c> chooser) via a
/// UI-thread post seam and returns its result to the caller (the
/// <see cref="Cobalt.Tui.Screens.TuiTextInput"/> pattern). Terminal.Gui's
/// <c>Run</c>/<c>Begin</c>/<c>End</c> have no thread-affinity guard,
/// so a background continuation must marshal onto the main loop before invoking any modal
/// (ADR 0020; #71).
/// </summary>
internal static class UiWork
{
    /// <summary>Posts <paramref name="work"/> via <paramref name="post"/> and returns its
    /// result. Continuations on the returned task run off the posting thread, matching
    /// <c>TaskCreationOptions.RunContinuationsAsynchronously</c>.</summary>
    internal static Task<T> RunAsync<T>(Action<Action> post, Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
