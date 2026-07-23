namespace Cobalt.Tui.Tasks;

/// <summary>
/// The typed SUPERSEDE primitive of ADR 0008: at most one logical in-flight fetch, where
/// scheduling a new key cancels and abandons the previous one and only the newest may publish.
/// Generalizes the <c>_loadSeq</c> stamp guard proven in <c>PrListViewModel.LoadTabAsync</c> —
/// cancellation is cooperative, so a fetch may ignore its token and still complete, and the
/// stamp comparison (not the cancel) is what keeps its stale result from landing. This is NOT
/// the diff dialog's join/dedup cache (the <c>ConcurrentDictionary</c>+<c>Lazy</c> single-flight
/// in ADR 0008 §"Single-flight diff fetches") — that shares one fetch between converging
/// callers; this one abandons the old fetch entirely.
/// </summary>
public sealed class SingleFlightCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _cts;
    private long _stamp;
    private bool _disposed;

    /// <summary>Every per-schedule token source is created linked to <paramref name="lifetime"/>,
    /// so the owner's shutdown cancels whatever fetch is in flight.</summary>
    public SingleFlightCache(CancellationToken lifetime) => _lifetime = lifetime;

    /// <summary>The monotonic supersede stamp, incremented once per schedule.</summary>
    public long Stamp
    {
        get { lock (_gate) { return _stamp; } }
    }

    /// <summary>
    /// Supersedes any in-flight fetch (cancelling its token) and runs <paramref name="fetch"/>;
    /// <paramref name="publish"/> is invoked only if this schedule is still the newest when the
    /// fetch completes, checked atomically with any concurrent schedule's stamp increment. The
    /// returned task completes successfully when the fetch published, was cancelled, or was
    /// superseded — cancellation is swallowed (<see cref="TaskCancellationExtensions.IgnoreCancellationAsync"/>
    /// semantics). A fault from the fetch that is still the newest propagates; a fault from a
    /// superseded fetch is observed and swallowed, so an abandoned fetch nobody awaits can never
    /// reach the <see cref="TaskScheduler.UnobservedTaskException"/> crash-log hook (ADR 0013;
    /// precedent: <c>PrDiffViewModel.ObserveFault</c>).
    /// </summary>
    public async Task ScheduleAsync(
        TKey key, Func<TKey, CancellationToken, Task<TValue>> fetch, Action<TKey, TValue> publish)
    {
        long stamp;
        CancellationToken token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stamp = ++_stamp;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            token = _cts.Token;
        }

        TValue value;
        try
        {
            value = await fetch(key, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // a superseded or shut-down fetch is never an error (IgnoreCancellationAsync semantics)
        }
        catch
        {
            lock (_gate)
            {
                if (stamp == _stamp)
                {
                    throw; // still the newest — the caller owns surfacing this fault
                }
            }
            return; // superseded — the fault is observed here (caught) and goes no further
        }

        lock (_gate)
        {
            if (stamp != _stamp)
            {
                return; // superseded while mid-flight — drop the result, even if the fetch ignored its token
            }
            publish(key, value);
        }
    }

    /// <summary>Cancels and disposes the current token source; idempotent. A schedule after
    /// dispose throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
