using Cobalt.Core.Models;
using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Fills in per-PR comment counts lazily in the background so a slow ADO never
/// blocks the list render. Counts are fetched only for the rows handed in (the
/// visible set), under a small concurrency cap, cached by
/// <c>(PullRequestId, LastMergeSourceCommit)</c>, and surfaced through
/// <see cref="CountAvailable"/> as each one lands. Failures and cancellations are
/// swallowed — enrichment is best-effort decoration, never a hard dependency.
/// </summary>
public sealed class PrCommentCountEnricher(
    Func<PullRequest, CancellationToken, Task<int>> fetch,
    int maxConcurrency = 5)
{
    private readonly SemaphoreSlim _gate = new(maxConcurrency, maxConcurrency);
    private readonly Dictionary<string, int> _counts = [];
    private readonly HashSet<string> _inflight = [];
    private readonly HashSet<string> _failed = [];
    private readonly object _lock = new();

    /// <summary>Raised (with the PR id) whenever a fresh count is cached.</summary>
    public event Action<int>? CountAvailable;

    /// <summary>
    /// Drops the cached counts and the failed-key set so a subsequent enqueue refetches them. Used
    /// on an explicit refresh (<c>r</c> / <c>:scope</c>) since the enricher outlives the list screen
    /// (CACHE-1), otherwise a new comment made without a re-push would keep its stale badge all
    /// session. In-flight fetches are left to complete and repopulate the cache.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _counts.Clear();
            _failed.Clear();
        }
    }

    /// <summary>The cached count for a PR, or <c>null</c> if it hasn't arrived yet.</summary>
    public int? TryGet(PullRequest pr)
    {
        lock (_lock)
        {
            return _counts.TryGetValue(Key(pr), out var count) ? count : null;
        }
    }

    /// <summary>Fire-and-forget batch enrichment for the view; swallows cancellation.</summary>
    public void Enqueue(IReadOnlyList<PullRequest> rows, CancellationToken ct) =>
        _ = EnrichAsync(rows, ct).IgnoreCancellationAsync();

    /// <summary>
    /// Awaitable batch enrichment (used by tests). Claims each uncached, not-in-flight
    /// PR, then fetches under the concurrency cap. Completes when the batch settles.
    /// </summary>
    public async Task EnrichAsync(IReadOnlyList<PullRequest> rows, CancellationToken ct)
    {
        List<PullRequest> toFetch = [];
        lock (_lock)
        {
            foreach (var pr in rows)
            {
                var key = Key(pr);
                // Skip keys already counted, already fetching, or that failed earlier this
                // session — the last guard stops a flaky route from re-firing on every render (L4).
                if (_counts.ContainsKey(key) || _failed.Contains(key) || !_inflight.Add(key))
                {
                    continue;
                }
                toFetch.Add(pr);
            }
        }
        if (toFetch.Count == 0)
        {
            return;
        }
        await Task.WhenAll(toFetch.Select(pr => FetchOneAsync(pr, ct))).ConfigureAwait(false);
    }

    private async Task FetchOneAsync(PullRequest pr, CancellationToken ct)
    {
        var key = Key(pr);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var count = await fetch(pr, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _counts[key] = count;
                    _inflight.Remove(key);
                }
                CountAvailable?.Invoke(pr.PullRequestId);
                return;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed fetch is non-fatal, but mark the key failed so it is not re-fetched on
            // every subsequent render — a flaky route would otherwise re-fire forever (L4).
            lock (_lock)
            {
                _failed.Add(key);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled (tab/scope switch, disposal): swallow and drop the claim so a later
            // load can retry — cancellation is not a fetch failure.
        }

        lock (_lock)
        {
            _inflight.Remove(key);
        }
    }

    private static string Key(PullRequest pr) =>
        pr.LastMergeSourceCommitId is { Length: > 0 } commit
            ? $"{pr.PullRequestId}:{commit}"
            : pr.PullRequestId.ToString();
}
