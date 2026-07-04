namespace Cobalt.Tui.Editor;

/// <summary>
/// Marshals the terminal-owning section onto the UI thread and parks the main
/// loop there for the child's whole lifetime. One callback is queued via
/// <paramref name="invokeOnUiThread"/>; on that thread it does
/// <c>suspend(); try { body(); } finally { resume(); }</c> and completes a
/// <see cref="TaskCompletionSource{T}"/> with the result or exception.
///
/// <para>Parking the loop inside the invoked callback is the guarantee that
/// Terminal.Gui cannot draw, relayout, or dispatch other invokes while the child
/// owns the terminal. Consequences: <see cref="RunSuspendedAsync"/> must never be
/// synchronously awaited (<c>.Wait()</c>) on the UI thread — that deadlocks; and
/// cancellation is not observed once the body starts (the token is only checked
/// before queuing the invoke).</para>
///
/// <para>Pure and fully unit-tested via injected delegates; the three Terminal.Gui
/// lambdas live in <c>TerminalGuiSuspender.For</c>.</para>
/// </summary>
public sealed class UiThreadSuspender(
    Action<Action> invokeOnUiThread, Action suspend, Action resume) : ITerminalSuspender
{
    public Task<int> RunSuspendedAsync(Func<int> body, CancellationToken cancellationToken = default)
    {
        // RunContinuationsAsynchronously keeps the awaiting continuation (in production,
        // the follow-up ADO call) off the UI thread: without it, TrySetResult would run
        // that continuation synchronously inside the invoke callback, on the parked loop.
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetException(new OperationCanceledException(cancellationToken));
            return tcs.Task;
        }

        invokeOnUiThread(() =>
        {
            // Re-check on the UI thread: between queuing and running, the caller's
            // dialog may have closed (cancelling its token). Skip so we never open an
            // editor over the shell after the requesting screen is gone.
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetException(new OperationCanceledException(cancellationToken));
                return;
            }

            var exit = 0;
            try
            {
                // resume() in the finally covers suspend() too, so a throw during
                // suspend still attempts to repair a half-suspended screen. The
                // result is set only after resume() returns, so a resume failure
                // faults the task instead of being silently lost.
                try
                {
                    suspend();
                    exit = body();
                }
                finally
                {
                    resume();
                }
                tcs.TrySetResult(exit);
            }
            catch (Exception ex)
            {
                // Covers a throw from suspend(), body(), or resume().
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
