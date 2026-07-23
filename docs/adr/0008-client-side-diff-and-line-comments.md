# 0008 — Client-side unified diff and line-comment anchoring

Status: Accepted · Date: 2026-07-04

## Context

PR review (SPEC §3, the flagship feature) needs a diff view and the ability to
comment on specific lines. Azure DevOps does not return a ready-made unified
diff; it exposes iterations, per-iteration change lists, and file content at a
commit. Line comments are created as threads carrying a `threadContext`
(`filePath` plus `rightFile*`/`leftFile*` line positions). This anchoring was
called out in the plan as the riskiest single item.

## Decision

- Compute the diff **client-side**: fetch the changed file's content at the
  iteration's base commit (`commonRefCommit`) and source commit
  (`sourceRefCommit`), then run `DiffPlex` (`InlineDiffBuilder`) to a unified
  line model. Added files diff against empty base; deleted files against empty
  source; a missing blob (404) is treated as empty.
- `DiffService` is pure and unit-tested (context/add/remove line numbering,
  addition/deletion counts, binary detection via NUL byte, large-file cap,
  trailing-newline normalization to git-style line semantics).
- **Line comments** anchor on the **right (new) side** for context/added lines
  and the **left (old) side** for removed lines, using a one-based
  `line`/`offset` position in `threadContext`. v1 anchors against the latest
  iteration only; cross-iteration thread tracking is post-v1.
- Per-file diffs are fetched lazily and cached; threads are mapped to lines by
  `filePath` + right line for gutter markers.

### Load concurrency and publish order

- A PR open costs ~3 round-trips, not 5: the base and source blobs are fetched
  concurrently, and the threads fetch starts alongside the first file's blobs
  (both need only data already in hand after the iteration-changes call).
- The threads fetch starts **after** the changes call rather than at the top of
  `LoadAsync`: the blobs cannot start any earlier regardless, and the
  no-iterations early return would otherwise abandon an in-flight task —
  unobserved faults reach the `TaskScheduler.UnobservedTaskException` hook and
  append a crash-log entry for a background fetch nobody was waiting on.
- **Threads are harvested before the diff is published**, and both land before a
  single paint. This ordering is load-bearing, not stylistic:
  - the view renders `Error` only while `CurrentDiff` is null, and otherwise
    overwrites the error header with the file's stats — so publishing the diff
    before threads land turns a threads-fetch failure into a clean-looking diff
    with no markers on a PR that *does* carry review comments;
  - `LoadAsync` runs once per dialog, so discarding a successful threads result
    when the first file's blob fails costs the whole session its markers.
- Publishing the diff before threads arrive (a "progressive paint") was tried and
  **rejected**: it saves no round-trips — threads and blobs are already
  concurrent, so it only shifts the paint by the difference between them — while
  costing the correctness above, a second full tokenize pass, and a window where
  the diff is interactive with no threads loaded.
- `CurrentDiff` and `CurrentDiffPath` are published as one immutable record in a
  single reference write. The view pairs the path with the diff to render its
  header, so a torn read would show one file's stats under another file's path.
  Two adjacent assignments are not sufficient: continuations run on the thread
  pool, so concurrent selections can interleave between them.
  - **Amendment (2026-07-22):** the write is atomic, but reading `CurrentDiff` and
    `CurrentDiffPath` back as two separate property reads re-introduces the tear on
    the *read* side. Any consumer that pairs the diff with its path **must** read
    `PrDiffViewModel.CurrentDiffSnapshot`, which snapshots the diff-state reference
    once and returns `(diff, path)` together. The view's `Render` / `WriteDiffHeader`
    / `NavThread` / `CommentAsync` all go through it; the single-property reads
    (`MarkViewed`/`MarkUnviewed`, `NavHunk`, `ApplySearch`) touch only one of the pair
    and so cannot tear.

### Single-flight diff fetches

- In-flight diff computations are shared through a
  `ConcurrentDictionary<string, Lazy<Task<FileDiff>>>`, so background prefetch and
  user navigation converging on one file issue a single pair of blob requests.
  Entries are evicted on success and failure alike, by value identity, so a
  failed file stays retryable and a late awaiter cannot evict a newer entry.
- **A joining caller is bound to the starter's cancellation token; its own token
  is never consulted.** This is safe only because every caller passes the dialog's
  token. It is enforced by convention at the call sites, not by the type system.
  A separate prefetch token would break it: prefetch starts a file, the user
  navigates to it and joins, prefetch cancels — and the user's own navigation is
  cancelled by a token they never passed, which the expected-error filters read as
  a genuine cancel and swallow, leaving a blank pane. Bounded prefetch therefore
  runs on the dialog token and does **not** cancel far files. If a second token is
  ever introduced, each caller must await via `WaitAsync(ct)` so awaiter-cancel is
  decoupled from fetch-cancel — which in turn needs the orphaned shared task's
  fault observed, or it reaches the crash-log hook.

### Background prefetch start (round 2, ASYNC-3)

- `PrDiffViewModel` raises a `FilesLoaded` signal the instant `Files` is assigned, before the
  threads fetch and the first file's diff are even requested. The dialog starts its background
  diff prefetch off this signal instead of waiting for the whole load to settle, so prefetch of
  the remaining files begins earlier.
- The dialog posts the prefetch start (`Task.Run`) rather than calling it inline from the
  `FilesLoaded` handler, so `LoadAsync` still issues its interactive threads-and-first-diff
  requests before the prefetch's blob wave competes for the connection — the earlier start must
  not cost first-paint latency to get there.
- Prefetch still runs through the single-flight `Lazy<Task<FileDiff>>` cache on the dialog's own
  token (above), so an earlier start changes nothing about which caller "wins" a file or which
  token governs cancellation — the single-flight and publish-order invariants are unchanged.

### PR-tab cached-then-refresh paint (round 2, CACHE-3)

- **Decision reversed:** the PR list's per-tab load used to blank the pane the instant a tab was
  pressed and repaint only once the fetch landed (call this the original "blank on switch"
  behaviour — see the `LoadTabAsync` code comment, which now documents both the old and new
  behaviour in place). `PrListViewModel` now caches each tab's last successful result and paints
  it immediately on revisit, then refreshes it under the existing `_loadSeq` guard — a tab
  visited earlier in the session goes straight from "loading" to its last-known rows instead of
  to blank.
- **Why:** switching tabs is the single most frequent action in the PR section, and re-fetching
  every time made every revisit pay a round-trip the user had already paid once this session,
  for rows that are usually still correct.
- **Tradeoff:** a revisited tab can show up to a few seconds of stale data while the background
  refresh is in flight — the same class of staleness the CACHE-1 shell-lifetime screens accept.
  `r` still forces a fresh fetch, and any of `:scope` change, project/context switch, or a
  mutation on the active tab invalidates that tab's (or every tab's) cache so a stale row is
  never left uncorrected. A tab not yet visited this session (or invalidated) still blanks and
  waits, matching the original behaviour for that one case.
- This does not touch the diff pane's own single-flight/publish-order invariants above; it is a
  separate cache one layer up, in the PR list rather than the diff view.

## Consequences

The diff surface is fully testable without a live service and independent of any
ADO diff endpoint. Cost: two blob fetches per file (mitigated by lazy load +
cache), and client-side diffs can differ cosmetically from the server's (e.g.
rename/whitespace heuristics). Binary and oversized files are skipped with a
visible marker rather than mis-rendered.

The load path buys its round-trips back through concurrency rather than through
partial rendering, so the pane goes from "loading" straight to complete: nothing
on screen is ever provisional, at the cost of showing the diff a fraction later
than the blobs alone would allow. The single-flight and publish-order invariants
are the price — both are invisible in the type signatures and enforced only by
the call sites, so changes to prefetch or selection need this ADR read first.
