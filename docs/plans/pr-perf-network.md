# PR diff network-path performance plan

> **Superseded in one place.** Step 2 originally paired the threads-fetch overlap
> with a *progressive paint* — publishing the first file's diff before threads
> landed. The overlap shipped; the progressive paint was **dropped**. It saved no
> round-trips (threads and blobs already run concurrently, so it only shifted the
> paint by the difference between them) and cost three correctness regressions: a
> threads-fetch failure rendered as a clean diff with no markers, a first-file
> blob failure discarded a good threads result for the whole session, and the diff
> was interactive with no threads loaded. See ADR 0008 → "Load concurrency and
> publish order". The ~3-RTT target was met without it.

## Findings (ranked by expected latency win)

1. **PR-open is 5 serial RTTs** — `src/Cobalt.Tui/ViewModels/PrDiffViewModel.cs:122-137`: `LoadAsync` chains iteration → changes → threads → first diff, and the first diff (`PrDiffViewModel.cs:315-321`) itself chains base blob → source blob. At ~120ms/RTT that is ~600ms, plus cold-connection setup (measured to dev.azure.com: DNS 0.51s, TLS 0.16s). `GetThreadsAsync` (`:132`) depends only on prId; the two blobs are independent.
2. **Every uncached file navigation is 2 serial RTTs** — `ComputeDiffForFileAsync` awaits `baseText` then `sourceText` (`PrDiffViewModel.cs:316-321`); `SelectFileAsync` (`:157-161`) blocks on it, so a cache-miss `j` stalls twice.
3. **Prefetch is sequential and order-blind** — `PrefetchAllDiffsAsync` (`PrDiffViewModel.cs:93-113`) walks `Files` in index order, one file at a time × 2 serial RTTs: a 40-file PR takes ~10s to warm, with no priority for files near the user's position.
4. **HTTP layer all-default** — `src/Cobalt.Core/Ado/AdoConnection.cs:38` `new SocketsHttpHandler()`. Verified defaults (probed live): `PooledConnectionIdleTimeout` 60s (a minute reading one diff → next request re-pays TLS), `AutomaticDecompression` None (no Accept-Encoding sent), infinite `PooledConnectionLifetime`, and no warm-up request — the first API call pays DNS+TCP+TLS.
5. **Duplicate in-flight fetches** — `_diffCache` (`PrDiffViewModel.cs:30, 308-311`) caches results, not tasks; prefetch + selection (or two rapid selects) on the same uncached file double-fetch.

## Plan

### Step 1: parallel blob pair  [commit: `perf(diff): fetch base and source blobs concurrently`]
- Files: `src/Cobalt.Tui/ViewModels/PrDiffViewModel.cs`, `tests/Cobalt.Tui.Tests/ViewModels/PrDiffViewModelTests.cs`
- Change: in `ComputeDiffForFileAsync`, start both `GetFileContentAsync` calls then await both (keep the Add/Delete empty-string short-circuits). Cache miss: 2 RTTs → 1.
- Test (RED first): fake whose `GetFileContentAsync` returns TaskCompletionSource-backed tasks; assert **both** blob requests are pending before releasing either (today only one is issued), then release and assert the diff.
- Risk: minimal — same token per request; the Polly retry (`AdoConnection.cs:31-34`) is per-request.

### Step 2: overlap threads + progressive first paint  [commit: `perf(diff): overlap threads fetch and paint diff before threads arrive`]
- Files: same two.
- Change: `LoadAsync` starts `threadsTask` first; runs iteration → changes → first diff; sets `IsLoading=false` and raises `Changed` (first paint shows the diff); then `Threads = await threadsTask` (the `finally` `Changed` fills in markers). Open: 5 serial RTTs → ~3.
- Test (RED first): gate `GetThreadsAsync` on a TCS; assert `CurrentDiff != null && !IsLoading` after a `Changed` while threads are still pending; release, assert `Threads` populated.
- Risk: view renders twice per open (see Handoffs); a threads failure now surfaces after first paint — same message-bar path, and the await stays inside the existing catch filters (ADR 0013 preserved).

### Step 3: single-flight diff computation  [commit: `perf(diff): share in-flight diff fetches instead of duplicating`]
- Files: same two.
- Change: add `ConcurrentDictionary<string, Lazy<Task<FileDiff>>> _inflight`; return the result-cache hit, else `GetOrAdd` the lazy task (`Lazy` prevents the GetOrAdd double-factory race). On success write `_diffCache` and evict; on failure evict so a later select retries after a prefetch skip (preserves `OneFileFailsSource` semantics, `PrDiffViewModelTests.cs:611`).
- Test (RED first): gated fake counting `GetFileContentAsync` calls; two `SelectFileAsync` on the same uncached file before release → exactly 2 blob calls, not 4. Plus: prefetch fails a file, subsequent select succeeds.
- Risk: shared-task cancellation — safe today because prefetch and selection both pass the dialog `Token` (`src/Cobalt.Tui/Screens/DiffReviewDialog.cs:466-479`); document that assumption.

### Step 4: prioritized bounded prefetch  [commit: `perf(diff): prefetch nearest-to-selection with bounded concurrency`]
- Files: same two; ADR 0008/0018 prefetch wording + CHANGELOG.
- Change: replace the `foreach` with 4 workers each pulling "nearest un-fetched file to `_selectedFileIndex`" from a small pure picker (lock-guarded fetched-set; stale index read is benign). Single-flight (step 3) means user navigation shares prefetch work automatically. `StatsChanged` per file unchanged.
- Test (RED first): gated fake logging request order over 8 Add-only files (1 blob each): first in-flight set is files 0–3; select index 7, release one gate → next request is adjacent to 7, not file 4.
- Risk: 429s — the Polly `HttpRetryStrategyOptions` is Retry-After-aware (`AdoConnection.cs:33`); cap at 4, not configurable.

### Step 5: HTTP transport tuning + warm-up  [commit: `perf(ado): connection warm-up, pooled-connection tuning, gzip`]
- Files: `src/Cobalt.Core/Ado/AdoConnection.cs` (+ `AdoHttp` for the route), `CobaltTuiApp.Run` call site, AdoHttp handler-stub tests.
- Change: `AutomaticDecompression = All`, `PooledConnectionIdleTimeout = 5 min`, `PooledConnectionLifetime = 15 min`; add `WarmUpAsync` (GET `_apis/connectionData`) fired-and-forgotten from the composition root after auth — saves the measured ~700ms DNS+TCP+TLS off the first real call.
- Test: handler-stub asserting the warm-up route and that expected failures are swallowed.
- Risk: decompression benefit unverified (below) but harmless; warm-up must never surface auth errors to the message bar.

## Verdicts on the 6 questions

1. **Parallelize `LoadAsync` / `ComputeDiffForFileAsync`: yes** — steps 1–2; open drops 5 → ~3 RTTs, navigation miss 2 → 1.
2. **Bounded + adjacent-priority prefetch: yes** — step 4; nearest-to-selection ordering gives adjacent-file priority for free.
3. **Prefetch yielding to navigation: no explicit machinery.** Prefetch caps at 4 connections; on-demand fires immediately on its own connection (`MaxConnectionsPerServer` default int.MaxValue — verified by probe); blob sizes make bandwidth contention negligible vs RTT. Single-flight covers the overlap.
4. **Share in-flight: yes** — step 3; also the mechanism that makes step 4 safe.
5. **HTTP/2: no** — verified live: a .NET client offering h2 to dev.azure.com negotiated HTTP/1.1. Warm-up, idle-timeout bump, gzip opt-in, finite pool lifetime: yes (step 5).
6. **Progressive load: yes** — cheap variant folded into step 2: the diff paints before threads land.

## Handoffs (render/input, not this plan's scope)

- `SelectFileAsync` keeps the previous file's `CurrentDiff` until the fetch lands — an instant "loading" echo on `j` is render UX.
- Step 2 raises `Changed` twice per open — render side may want to tolerate/dedupe.

## Unknowns

- ADO gzip on **authenticated** API responses: unverified — an anonymous endpoint returned 133KB uncompressed despite `Accept-Encoding: gzip`; enabling decompression is free either way, confirm benefit via `tools/uat`.
- Warm per-request RTT to the owner's org: unverified, ~100–150ms assumed.
- HTTP/2 probe hit the root URL from this machine; org-scoped API is the same host — same result assumed, not separately verified.
