namespace Cobalt.Tui.Tasks;

/// <summary>
/// The typed JOIN primitive of ADR 0008: converging callers share one in-flight fetch per key,
/// and a successful result is cached for the cache's lifetime. The inverse of
/// <see cref="SingleFlightCache{TKey,TValue}"/>, which supersedes — here the FIRST fetch wins and
/// later callers attach to it; no joiner ever cancels the shared fetch.
///
/// <para>The contract, in one place instead of once per adapter:</para>
/// <list type="bullet">
/// <item>The fetch is <em>start-detached</em> — <c>start</c> takes no token by design, so it can
/// never observe the starting caller's. Every caller, starter included, awaits via
/// <see cref="Task.WaitAsync(CancellationToken)"/>, so one caller's cancellation cancels only its
/// own await and leaves the shared fetch running for the others.</item>
/// <item>An <em>unsuccessful</em> fetch is evicted and retried — faulted <em>or</em> canceled,
/// because an HttpClient timeout surfaces as a canceled task rather than a fault, and caching one
/// would rethrow it for the rest of the session with no network.</item>
/// <item>Eviction is attached to the shared fetch, not to any caller's await, so a fault nobody is
/// left awaiting is still observed rather than reaching the
/// <see cref="TaskScheduler.UnobservedTaskException"/> crash-log hook (ADR 0013).</item>
/// <item>Eviction is <em>by identity</em>, and a completed-unsuccessfully entry counts as absent on
/// lookup, so a late eviction can never remove a newer attempt and a retry is never poisoned.</item>
/// </list>
///
/// <para>Deliberate NON-user: <c>PrDiffViewModel</c>'s per-file diff dedup. It differs on two
/// ADR-0008-pinned axes — its fetch runs on the starter's caller token (so closing the diff dialog
/// cancels the in-flight blob requests) and its entries are evicted on success too, being a
/// transient dedup whose results live in a separate cache. See ADR 0008 §"Single-flight diff
/// fetches"; serving it from here would take mode parameters, so it keeps its own copy.</para>
/// </summary>
public sealed class JoinFlightCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Task<TValue>> _entries = [];

    /// <summary>
    /// Returns the cached result for <paramref name="key"/>, joins the fetch already in flight for
    /// it, or starts one with <paramref name="start"/> — awaited through <paramref name="ct"/>, which
    /// governs only this caller's await.
    /// </summary>
    public Task<TValue> GetOrJoinAsync(TKey key, Func<TKey, Task<TValue>> start, CancellationToken ct)
    {
        Task<TValue> shared;
        lock (_gate)
        {
            // A cached entry that has already ended unsuccessfully counts as absent: its eviction
            // continuation is only best-effort-synchronous, so a deferred one would otherwise leave
            // a fetch nobody can succeed with briefly cached, costing the next caller one certain
            // failure. Starting fresh here closes that window.
            if (!_entries.TryGetValue(key, out var existing) || IsUnsuccessful(existing))
            {
                existing = start(key);
                // Store the entry *before* attaching the eviction, so an already-completed
                // unsuccessful fetch evicts itself right here via the synchronous continuation
                // instead of being cached before eviction can see it.
                _entries[key] = existing;
                _ = existing.ContinueWith(
                    task => EvictIfUnsuccessful(key, task),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            // Local copy: the eviction above can run inline and remove the entry, which must not
            // change which task this caller joins.
            shared = existing;
        }
        return shared.WaitAsync(ct);
    }

    private static bool IsUnsuccessful(Task<TValue> fetch) =>
        fetch.IsCompleted && !fetch.IsCompletedSuccessfully;

    private void EvictIfUnsuccessful(TKey key, Task<TValue> fetch)
    {
        if (fetch.IsCompletedSuccessfully)
        {
            return;
        }
        _ = fetch.Exception; // observe a fault (a canceled task carries none)
        lock (_gate)
        {
            // Evict by identity: a late continuation must never remove a newer attempt.
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, fetch))
            {
                _entries.Remove(key);
            }
        }
    }
}
