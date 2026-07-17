# 0012 — Lazy, background per-PR comment counts

Status: Accepted · Date: 2026-07-06

## Context

The PR list wants a comment-count badge per row (`💬 3`). Azure DevOps has no
list-level count: the number of comments is only obtainable per PR via
`GET …/pullRequests/{id}/threads`, which is one HTTP round-trip **each**. Under
org scope a list can hold up to 200 PRs, so fetching counts inline during the list
load would multiply one list request into hundreds of serial calls and make the
list feel broken when ADO is slow — exactly the pane we most need to stay instant.

The user's product choice for this trade-off was **lazy background fill**: render
the list immediately with no counts, then fill badges in asynchronously.

## Decision

- **Enrichment is a separate, injectable unit** — `PrCommentCountEnricher`
  (Cobalt.Tui/ViewModels). It takes a `Func<PullRequest, CancellationToken, Task<int>>`
  count-fetcher, so tests drive it with a fake and it never depends on real HTTP.
  The production fetcher (wired in `CobaltShell`) calls the adapter's `GetThreadsAsync`
  and sums non-system comments (`thread.Comments.Count(c => !c.IsSystem)`).

- **Never block the initial render.** The list `Format` gained an optional
  count lookup from the enricher's cache; when a count is absent the badge is simply
  omitted and the row renders instantly. The view triggers enrichment *after* each
  render (fire-and-forget) for the currently loaded rows.

- **Capped, cached, cancellable.** Fetches run under a small concurrency cap
  (`SemaphoreSlim`, default 5). Results are cached by
  `(PullRequestId, LastMergeSourceCommit)` — the merge commit keys the cache to the
  PR's current tip so a re-pushed PR re-counts, falling back to the id when the DTO
  has no commit. In-flight keys are tracked so the same PR is never fetched twice.
  Each list load (tab switch, `:scope` change, refresh) runs under its own per-load
  `CancellationTokenSource`, chained to the view's lifetime token: the view cancels and
  replaces it on every tab/scope switch, so a new load abandons the previous tab's
  pending fetches instead of leaving up to 200 stale calls hogging the semaphore; view
  disposal cancels the lifetime token (and with it any live load).

- **Failures are invisible but not retried forever.** A cancelled fetch is swallowed
  (the `IgnoreCancellationAsync` helper at the batch boundary) and drops its claim so a
  later load can retry; a *failed* fetch is swallowed too but marks the key failed for
  the session, so a flaky route is not re-fetched on every re-render. Enrichment is
  best-effort decoration, never a hard dependency.

- **Counts re-render their row.** The enricher raises `CountAvailable(prId)` as each
  count lands; the view marshals a `Render()` onto the UI thread via `IApplication.Invoke`.
  Re-rendering re-enqueues the loaded rows, but cached/in-flight/failed keys are skipped,
  so there is no fetch loop.

## Consequences

- The list is fast even against a slow ADO: the network cost of counts is paid off
  the critical path, bounded by the concurrency cap, and paid once per PR tip.
- Badges appear progressively rather than all at once — acceptable for decoration.
- The enricher is pure of Terminal.Gui, matching the view-model boundary of ADR 0004,
  and is unit-tested directly (fetch-only-requested, cap, cache, event, cancellation).

### Viewport-scoped enrichment (round 2, CACHE-2) — the pre-blessed follow-up, now done

v1 enriched *all currently loaded rows* rather than tracking the precise visible range,
flagged above as a follow-up. That follow-up is done:

- Only the on-screen rows (plus a small margin) are enqueued for enrichment, rather than every
  row the list has loaded.
- The view re-enqueues on the list's `ViewportChanged` event, confirmed by probe to fire on
  vertical scroll in Terminal.Gui 2.4.16, with a top-up on plain selection moves for the case a
  move doesn't itself change the viewport.
- The enricher's existing dedupe (cached/in-flight/failed keys are skipped) means scrolling back
  over already-enriched rows never re-fetches; this changes only which rows are *queued*, not the
  cap, cache keying, or failure handling above.
- The comment-count cache itself is now shell-lifetime rather than per-load: it survives a
  section switch (see CACHE-1) and is invalidated on `r` (refresh), a `:scope`/project/context
  change, or a mutation — the same invalidation points the per-tab PR cache uses (ADR 0008,
  CACHE-3), so a count never outlives the event that could have changed it.
