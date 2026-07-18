# Performance round 2 — spec

Follows PR #11. Every item below survived a Fable performance review of the whole codebase **plus**
two independent adversarial passes (Codex + a Fable skeptic) that verified each against the real code,
the ADRs, and the Terminal.Gui 2.4.16 package. Impact ratings are the reconciled (deflated) ones, not
the review's originals. Working notes: `scratchpad/perf-findings-raw.md`, `perf-findings-verdict.md`.

**House rules (all units):** TDD, test-first. Assert the *structural* property, never a raw speed number —
the repo's established style: `Assert.Same` for reuse/memoization, call-count / ordering assertions via
`FakeHttpHandler` and TCS-gated fakes, headless view tests driving `NewKeyDownEvent`. Behaviour parity
tests must stay green. Surgical changes only. `UI logic never touches Terminal.Gui` (ADR 0004).

## Locked cross-unit contract (fix before fan-out)

Unit C (render) and Unit E (input) consume APIs that Units D and B own. These signatures are frozen:

- **`PrDiffViewModel`** (owned by D) gains:
  - `IReadOnlySet<string> UnresolvedFilePaths { get; }` — recomputed once per `Threads` write in `HarvestThreadsAsync`.
  - A chrome-only busy signal so mutations don't force a full diff-pane rebuild (mirror the existing
    `StatsChanged` seam): `event Action? BusyChanged`. `Changed` keeps its meaning; the busy flip raises `BusyChanged`.
  - A `FilesLoaded` signal (event or awaitable) raised once `Files` is assigned, so prefetch can start earlier.
- **`AdoConnection` / identity** (owned by B) gains: `Task<AdoUser> GetIdentityAsync(CancellationToken)` — a
  reset-on-fault single-flight cache, plus `Task PrimeIdentityAsync()` that swallows expected faults (ADR 0013).
  The warm-up primes it; the old `Lazy` and `WarmUpAsync` collapse into it.

## Work units (disjoint file ownership → parallel-safe)

### Unit A — Core text algorithms  *(Sonnet)*
Files: `Cobalt.Core/Text/{IntraLineDiff,DiffFold,SideBySideComposer,DiffStyleCache}.cs`,
`Cobalt.Core/Text/Syntax/SyntaxTokenizer.cs`.
- **ALGO-1** (Med) — length-ratio early-out before the word-level Myers diff. Derive the threshold from
  `MaxChangedRatio` (don't hard-code 0.4); bit-for-bit identical output. TDD: allocation-bound test around a
  mismatched-length pair (RED now) + randomized parity vs a reference computed without the early-out.
- **ALGO-2** (Low) — `SyntaxToken` → `readonly record struct`; pre-size the token list. Parity tests unchanged.
- **ALGO-3** (Low) — keyword sets → `FrozenSet<string>`; `GetAlternateLookup<ReadOnlySpan<char>>()` on
  `line.AsSpan(...)`; delete the per-identifier substring.
- **ALGO-4** (Low) — cache one static `LangSpec` per language instead of building per `Tokenize` call.
- **RENDER-5** (Low) — memoize the immutable `DiffFoldState.Rows()` in a field (`Assert.Same` test); cache
  `SideBySideComposer.Pair` keyed on the lines reference.

### Unit B — Core ADO transport  *(Opus)*
Files: `Cobalt.Core/Ado/{AdoHttp,IdentityApi,AdoConnection,WorkItemsApi,WorkItemStoreAdapter,JsonPatchBuilder,WiqlBuilder}.cs`,
`Cobalt.Core/Models/WorkItemModels.cs`.
- **NET-1** (Med, *safe tier only*) — deserialize from `Content.ReadAsStreamAsync()` (content is buffered by
  default, no timeout change); read the string only on the error path for `ExtractError`. Preserve 203 mapping +
  empty-body exception. **Do NOT do the `ResponseHeadersRead` stretch tier** (changes `HttpClient.Timeout` coverage).
- **NET-2** (Med) — reset-on-fault single-flight identity cache in `AdoConnection`/`IdentityApi` (the frozen API
  above); warm-up primes it. Cold start then makes **one** `connectionData` call, not two. (Tui wiring is Unit E.)
- **NET-4** (Med) — cache work-item-type states per `(project,type)` in `WorkItemStoreAdapter`
  (`ConcurrentDictionary<_, Lazy<Task<...>>>`, fault-evict). Core stays stateless.
- **NET-5** (Low) — `Task.WhenAll` the independent `workitemsbatch` pages; merge then keep the WIQL-order re-sort;
  `GetRange` slicing; `TryGetValue`. (The review's "O(n²) chunker" sub-claim was wrong — `List` takes the IList
  fast path — ignore it.)
- **NET-6** (Low, **PRODUCT — approved**) — add `$top=200` to the WIQL (matches `GitApi.ListTop`). Silent
  truncation of the WI list; surface a "showing first N" affordance if cheap. Assert the request URI carries `$top`.
- **NET-7** (Low) — `SerializeToUtf8Bytes` + `ByteArrayContent` with explicit `charset=utf-8`; keep the wire
  Content-Type header byte-identical. Non-ASCII round-trip test.
- **NET-8** (Low) — project `WorkItem`'s known fields into readonly members in the ctor (materialize Tags once);
  retain the raw `JsonElement` dict only for detail reads (keep the public `GetString(field)` escape hatch working).

### Unit C — Diff-review render  *(Opus)*  — depends on D's VM contract
Files: `Cobalt.Tui/Screens/{DiffReviewDialog,DiffListDataSource,VimScroll}.cs`.
- **RENDER-1** (Med) — add an `includeFileList` gate (mirror the existing `includeDiffPane` gate) so fold-expand,
  search apply/clear, mode-toggle, and cross-fold `n`/`N` skip the file-tree re-flatten + row re-format. **Must
  still rebuild on annotation-changing paths** (comment resolve, mark-viewed, filter, stats refresh).
- **RENDER-2** (Med, dialog half) — `BuildAnnotations` probes `vm.UnresolvedFilePaths` (from D) instead of
  `Threads.Any(...)` per file.
- **RENDER-3** (Low) — cache each run's unclipped display slice; `Substring` only when clipped by viewportX/width
  (`AddStr(string)`-only is **verified** — no span overload); hoist the role `Color?[]` to a reused field cleared
  per row.
- **RENDER-4** (Low, dialog half) — consume D's `BusyChanged` to render chrome-only on the busy flip; the trailing
  render still fully rebuilds. Keep the Error-header refresh on the busy render (skeptic's catch).
- **RENDER-6** (Low) — special-case ListView in `VimScroll` (like TextView already is): clamp the target index once,
  set `SelectedItem` directly, ensure visible. **PROBE the 2.4.16 ensure-visible API first — do not guess.**
- **RENDER-7** (Low) — build an `int→rowIndex` map when `_diffRows` is (re)built; make `IsLineVisible` /
  `SelectDiffLine` / `CurrentUnifiedLine` O(1) lookups.

### Unit D — PR view-models & shell  *(Opus)*
Files: `Cobalt.Tui/ViewModels/{PrDiffViewModel,PullRequestStoreAdapter,PrListViewModel,PrCommentCountEnricher,WorkItemDetailViewModel}.cs`,
`Cobalt.Tui/Screens/PrListView.cs`, `Cobalt.Tui/App/CobaltShell.cs`.
- **RENDER-2** (Med, VM half) — add `UnresolvedFilePaths` (one O(threads) pass in `HarvestThreadsAsync`); make
  `FilteredFiles` probe it and snapshot once per render use.
- **RENDER-4** (Low, VM half) — add the `BusyChanged` seam (per the frozen contract).
- **STATE-2** (Low) — `Interlocked` running totals bumped when `_diffCache.TryAdd` returns true (fix the per-joiner
  indexer write); `Volatile.Read` getters.
- **ASYNC-2** (Low) — `Lazy<Task<TeamDirectory>>` recreated on fault, callers `WaitAsync(ct)` (start-detached so
  joiners aren't bound to a cancelled starter — ADR 0008); start the Active-list fetch before awaiting the directory.
- **ASYNC-3** (Low) — start prefetch from D's `FilesLoaded` hook instead of after full `LoadAsync`.
- **NET-3** (Med) — `WorkItemDetailViewModel`: `Task.WhenAll(comments, states)` after the item lands (keep the
  `_project` refinement); observe faults (house `ObserveFault` pattern).
- **CACHE-1** (Med + PRODUCT) — hoist `PrCommentCountEnricher` to `CobaltShell` field scope so its counts cache
  survives section toggles (unconditional win). **PRODUCT (approved):** keep the PR/WI screens alive across section
  switches (build lazily, toggle Visible/Add-Remove, don't Dispose) so rows aren't refetched — rows may be seconds
  stale until a background refresh; `r` still forces fresh.
- **CACHE-2** (Low) — enqueue only the viewport slice (+margin) to the enricher; re-enqueue on `ViewportChanged`.
  **PROBE whether ListView raises `ViewportChanged` on scroll in 2.4.16** — if not, top up on selection moves.
  (Not a contract bug — ADR 0012 pre-blessed this as a follow-up.)
- **CACHE-3** (Med, **PRODUCT — approved**) — per-tab last-result cache; paint cached rows instantly then refresh
  under the existing `_loadSeq` guard. **Conflicts with the documented D1 "blank on switch" decision → update the
  D1 note / add an ADR.** Invalidate on scope/project/context change.
- **MISSED-A** (Low, PrListView half) — stop re-formatting every row on every render when rows/filter are unchanged.

### Unit E — Input, startup, list views  *(Sonnet; STARTUP-1 probe-gated)*
Files: `Cobalt.Tui/Input/{KeymapRouter,KeyBindingTable}.cs`, `Cobalt.Tui/App/{KeyTokenizer,CobaltTuiApp}.cs`,
`Cobalt.Tui/Screens/WorkItemListView.cs`.
- **NET-2 wiring** — replace the warm-up `Lazy` in `CobaltTuiApp` with B's primed identity cache (frozen API).
- **INPUT-2** (Low) — cache per-scope binding arrays in `KeyBindingTable`; index-loop compare in the router
  (preserve scoped-before-global precedence). Router stays pure — full suite green + a steady-state allocation test.
- **INPUT-3** (Low) — mask `KeyCode` modifier bits instead of building stripped `Key` copies; static token tables
  for ASCII + `C-a..C-z`. KeyTokenizer stays the single Key→token seam (ADR 0007).
- **INPUT-4** (Low) — `public static KeyBindingTable Shared { get; } = Default();` treated as immutable; switch the
  6 call sites (or share the shell router's `Table`).
- **MISSED-A** (Low, WorkItemListView half) — same per-render row-reformat guard as PrListView.
- **STARTUP-1** (Low, **PROBE FIRST**) — measure idle CPU at 60 vs ~20 Hz (`MaximumIterationsPerSecond`) over ~30s.
  **Only if there is measurable idle cost**, add a pure `IterationRate(sinceLastInput)` decider (60 Hz while input
  flows, decayed idle, restore 60 on first key) + thin wiring. If the probe shows nothing to save, record the
  measurement and make no change.

### Unit F — INPUT-1 + SHELL-2  *(folded into Unit D — same file `CobaltShell.cs`)*
- **INPUT-1** (Med, **UAT-gated**) — targeted redraw on vim movement: `SetNeedsDraw()` the moved list view, then
  `LayoutAndDraw(false)` instead of `force:true`. **Must be UAT'd on both the `windows` and `dotnet` drivers
  (ADR 0016)** — the `force:true` dodged a driver dirty-flag quirk; document the new invariant.
- **SHELL-2** (Low) — `SetIfChanged(label, text)` guard in `RefreshChrome`; stop dirtying all three chrome labels
  on every message.

## Docs to update on completion
CHANGELOG; ADR 0008 (CACHE-3 D1 change / prefetch-start), ADR 0012 (CACHE-2 viewport), ADR 0016 (INPUT-1 redraw
invariant); README "Known limitations" if NET-6 truncation is user-visible. New ADR only if a decision is genuinely
new (CACHE-3 is the candidate).

## Out of scope
ASYNC-1 (dropped — contradicts ADR 0008, Codex-rejected). NET-1 stretch tier. Progressive paint (ADR 0008, rejected).
