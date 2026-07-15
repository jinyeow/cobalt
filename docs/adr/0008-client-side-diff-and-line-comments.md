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
