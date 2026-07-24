using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Identity of a previewable list row: the section it came from, its id, and its own project
/// (org scope may span projects, so the project is part of the identity — H1). Captured at
/// schedule time and carried through the whole pipeline, because the cursor's item and the
/// DISPLAYED item are different things while a fetch is in flight (ADR 0024 clause 2).
/// </summary>
public sealed record ItemKey(AppSection Section, long Id, string? Project);

/// <summary>
/// Everything the preview pane shows, as one immutable value: which item it belongs to,
/// whether it is the fetched detail (tier 2) or the list row's own data (tier 1), and the
/// composed text. Published in a single reference write so a torn "item A's title over item
/// B's reviewers" is impossible by construction (ADR 0024 clause 1).
/// </summary>
public sealed record PreviewState(ItemKey Key, bool Detailed, string Text);

/// <summary>
/// The two-tier preview load (ADR 0024 fork B) and the load invariant that governs it.
/// Tier 1 paints synchronously from the data the list row already holds — zero fetches, so the
/// pane is never blank while the cursor moves. Tier 2 fetches the full detail only once the
/// cursor settles, the debounce being served by the injected <see cref="TimeProvider"/>.
/// <para>The invariant is delegated to the H3 primitives rather than re-hand-rolled: the debounce
/// runs INSIDE the single-flight fetch, so a new key cancels the pending wait before any fetch is
/// enqueued (clause 4), <see cref="SingleFlightCache{TKey,TValue}"/> supplies cancel-superseded,
/// the newest-stamp guard and fault observation (clauses 2/3), and <see cref="Published{T}"/>
/// supplies the atomic publish (clause 1).</para>
/// <para>UI-free (ADR 0004). <see cref="Changed"/> may fire on a threadpool continuation, so a
/// subscriber that touches Terminal.Gui must marshal through <c>IUiPost</c>.</para>
/// </summary>
public sealed class PreviewViewModel : IDisposable
{
    /// <summary>Cursor-settle delay before the tier-2 fetch. 200 ms is the #43 prototype's UAT-confirmed feel.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    private readonly Func<ItemKey, CancellationToken, Task<string>> _fetchDetail;
    private readonly SingleFlightCache<ItemKey, string> _cache;
    private readonly Published<PreviewState> _state = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    // Set on the UI thread by Dispose; ShowAsync/Clear are UI-thread-only, so a plain field
    // suffices. Guards the use-after-dispose a UI post queued before teardown would otherwise hit.
    private bool _disposed;

    /// <param name="fetchDetail">Composes the previewed item's full detail text; one fresh detail
    /// view-model per item (ADR 0024 — no shared mutable view-model between pane and dialog).</param>
    /// <param name="lifetime">The workspace-lifetime token every per-schedule token is linked to.</param>
    /// <param name="time">Drives the cursor-settle debounce; tests inject a fake clock.</param>
    /// <param name="debounce">Settle delay before tier 2; defaults to <see cref="DefaultDebounce"/>.</param>
    public PreviewViewModel(
        Func<ItemKey, CancellationToken, Task<string>> fetchDetail,
        CancellationToken lifetime,
        TimeProvider? time = null,
        TimeSpan? debounce = null)
    {
        _fetchDetail = fetchDetail;
        _cache = new SingleFlightCache<ItemKey, string>(lifetime);
        _time = time ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>The state on screen, from a single read — snapshot it once and destructure rather
    /// than reading twice, or the tear returns on the read side.</summary>
    public PreviewState? Current => _state.Current;

    /// <summary>Raised after a publish (tier 1 or tier 2). May fire on a threadpool continuation.</summary>
    public event Action? Changed;

    /// <summary>
    /// Shows <paramref name="key"/>: paints <paramref name="summary"/> (the row's own data)
    /// before returning, then schedules the debounced tier-2 fetch, superseding whatever the
    /// previous key had pending. Re-showing the item already on screen is a no-op, so a list
    /// re-render cannot restart the pipeline. The returned task completes when tier 2 published,
    /// was superseded, or was cancelled — the caller observes it (<c>FireAndForget.Observe</c>).
    /// </summary>
    public Task ShowAsync(ItemKey key, string summary)
    {
        if (_disposed || (_state.Current is { } current && current.Key == key))
        {
            return Task.CompletedTask;
        }
        Publish(new PreviewState(key, Detailed: false, summary));
        return LoadDetailAsync(key);
    }

    /// <summary>
    /// Clears the pane (nothing selected, or the preview collapsed) and cancels any pending tier-2
    /// fetch — a hidden pane must spend no round-trip (ADR 0024). The cancel stops the debounce
    /// before a fetch is even issued; a fetch already running is cancelled and its result dropped.
    /// </summary>
    public void Clear()
    {
        if (_disposed || _state.Current is null)
        {
            return;
        }
        _cache.Cancel();
        Publish(null);
    }

    private async Task LoadDetailAsync(ItemKey key)
    {
        var published = false;
        await _cache.ScheduleAsync(
            key,
            async (k, ct) =>
            {
                // The debounce lives inside the fetch so the single-flight cancels it: while the
                // cursor moves, each new key cancels the previous wait and NO fetch is ever enqueued.
                await Task.Delay(_debounce, _time, ct).ConfigureAwait(false);
                return await _fetchDetail(k, ct).ConfigureAwait(false);
            },
            // Runs under the cache's lock (that lock is the atomicity guarantee), so it stays a
            // reference swap; Changed is raised below, outside the lock. The key check is clause 2
            // stated directly: publish only over the item still DISPLAYED, so a completion for an
            // item the pane has since cleared repaints nothing.
            (k, text) =>
            {
                if (_state.Current is not { } shown || shown.Key != k)
                {
                    return;
                }
                _state.Publish(new PreviewState(k, Detailed: true, text));
                published = true;
            }).ConfigureAwait(false);
        if (published)
        {
            Changed?.Invoke();
        }
    }

    private void Publish(PreviewState? state)
    {
        _state.Publish(state);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Dispose();
    }
}
