# PR review perf — render + input path plan

## Findings (ranked by expected key-to-paint win)

1. **Full-file re-tokenize + recompose on every content `Render`** — `src/Cobalt.Tui/Screens/DiffReviewDialog.cs:986-1003` (unified) / `:964-970` (side-by-side). Every `Render(includeDiffPane:true)` calls `SyntaxTokenizer.Tokenize` + `DiffLineStyler.Compose` per visible row, and `HasThread(line)` → `vm.ThreadsForDiffLine` (`PrDiffViewModel.cs:165-180`), O(threads) LINQ **plus a list alloc per row**. Cost: O(visible_rows × line_length) + O(rows × threads) per keypress. Triggered by single keys: `m`/`M`, `e`/`E`, `s`, `T`, search apply, `n`/`N`/`]c` crossing a fold, every comment/thread round-trip. After `E` on a 10k-line file: ~10k tokenizes + 10k×threads predicate evals + ~100k record allocs per keypress — discernible.
2. **`m`/`M` rebuild the diff pane for a file-tree-only change** — `DiffReviewDialog.cs:774-795` call full `Render()`; mark-viewed only changes file-tree glyphs.
3. **Per-run attribute resolution in the paint hot path** — `DiffListDataSource.cs:91` calls `GetAttributeForRole` per non-gutter run. Decompiled TG 2.4.16 `View.GetAttributeForRole`: scheme resolution + an **unconditional `VisualRoleEventArgs` heap alloc per call**. 30-50 runs/line × ~40 visible rows ≈ 1-2k calls+allocs per paint, i.e. per `j`/`k`; `Substring` per run (`:93`) adds 1-2k more. The only remaining per-scroll-keystroke cost; reducible ~10×. (`_palette()` = static field read — not a cost.)
4. **StatsChanged burst during prefetch** — one event per file (`PrDiffViewModel.cs:111`) → per event: `RebuildFileList` (Flatten + `BuildAnnotations` O(files×threads) + FormatRow all rows) + `TitleFor` reading `TotalAdditions`/`TotalDeletions`, each O(n) over `ConcurrentDictionary.Values` (snapshot copy per call). N files → N× that on the UI thread while the reviewer scrolls. #9 skipped the pane rebuild but did not coalesce the chrome refreshes.
5. **`IntraLineDiff` has no line-length guard** — `IntraLineDiff.cs:26`: DiffPlex Myers (O(ND), ~quadratic in word count worst case) on arbitrarily long lines; the similarity guard runs *after* the compute. Hits file-switch latency and prefetch CPU on minified lines.

## Plan

### Step 1: mark viewed/unviewed skips the diff pane  [perf(tui): skip diff-pane rebuild on mark viewed/unviewed]
- Files: `DiffReviewDialog.cs`
- Change: `MarkCurrentViewed`/`MarkCurrentUnviewed` → `Render(includeDiffPane: false)`.
- Test (RED): headless dialog, press `m`, assert `DiffPane.Source` reference-equal before/after (today: new instance).
- Risk: none — viewed state renders only in the file tree.

### Step 2: per-file styled-line cache + precomputed thread-line set  [perf(tui): cache per-file diff styling across renders]
- Files: new `src/Cobalt.Core/Text/DiffStyleCache.cs` (pure, ADR 0004-clean), `DiffReviewDialog.cs`, `PrDiffViewModel.cs`
- Change: cache per line index: tokens + composed `StyledLine` keyed by hasThread flag; invalidate on file path / mode / columnWidth change. Search hits stay a `WithSearchHits` overlay on the cached base (never cached in). Replace per-line `ThreadsForDiffLine` with a per-render `HashSet<int>` of left/right commented line numbers built once from `vm.Threads` (new pure VM method).
- Test (RED): unit — two successive compositions return reference-equal `StyledLine`s; flipping one line's thread flag recomposes only that line. Headless — after `e`, previously visible rows reference-equal.
- Risk: theming safe — `StyledLine` holds roles/kinds, colors resolve at paint (ADR 0019 intact). Watch: search overlay leaking into the cache.

### Step 3: hoist attribute/palette lookups in the row renderer  [perf(tui): resolve theme attributes once per row render]
- Files: `DiffListDataSource.cs` (+ `ThemeService.cs` only if caching per instance)
- Change: resolve Normal + the 8 `Code*` role attributes once per `Render` call (small array), not per run; read `_palette()` once per call and pass into `Map`. Per-render-call resolution keeps `:theme` live automatically; a per-instance cache needs a `ThemeService.Version` bumped in `Apply`.
- Test (RED): subscribe TG's `GettingAttributeForRole` event, render one multi-run row, assert invocations ≤ distinct roles (today = run count).
- Risk: ADR 0019 live `:theme` — prefer per-render-call resolution.

### Step 4: coalesce stats-prefetch chrome refreshes  [perf(tui): coalesce stats-prefetch chrome refreshes]
- Files: `DiffReviewDialog.cs`
- Change: pending-flag gate in `OnStatsChanged` — first event queues one `app.Invoke` refresh, later ones no-op until it runs; compute the totals once per `TitleFor`.
- Test (RED): extract the gate as pure logic and unit-test it (headless App never drains `Invoke`); wiring stays thin.
- Risk: clear the flag *before* rendering so a late event isn't lost.

### Step 5: length-guard IntraLineDiff  [perf(core): cap intra-line diff to sane line lengths]
- Files: `IntraLineDiff.cs`
- Change: before `CreateDiffs`, if either line > ~2000 chars return `([], [])`.
- Test (RED): `Compute` on two >2000-char differing lines returns empty spans.
- Risk: no word emphasis on very long lines — consistent with the existing rewrite guard's intent.

## Verdicts on the 5 questions
1. Yes — biggest win; Steps 1–2.
2. No guard; Myers worst-case ~quadratic in word count. Runs once per file (DiffService), not per render — Step 5.
3. No double-paint found (`j`/`k` → `MoveDown` → single `SetNeedsDraw`; `Render` ends in coalesced `SetNeedsDraw`s). But full rebuilds where a redraw would do: `m`/`M` (Step 1) and every content re-render re-tokenizing (Step 2).
4. `DiffSearch.Find` once per apply; `n`/`N` reuse `_searchMatches` — fine. `DiffFold.BuildBlocks` once per file, `Rows()` O(rows)/render — fine. Search *apply* triggers the full re-tokenize (fixed by Step 2).
5. Reject — tokenizer ≤1 small string per key; router ~2 LINQ enumerators per binding (~50 bindings) per key. Gen0 noise, imperceptible.

## Handoffs (network/data, not mine)
- `ComputeDiffForFileAsync` fetches base+source blobs serially per file and the prefetch is serial across files (`PrDiffViewModel.cs:93-113,316-321`) — sibling's scope.
- **Coordination with the network plan**: (a) `LoadAsync` raising `Changed` twice per open is fine render-side — with Step 2's cache the second (threads-arrived) render recomposes only commented lines. (b) `SelectFileAsync` keeping the previous file's `CurrentDiff` visible **does need render-side handling**: `Render` pairs `vm.SelectedFile` with `vm.CurrentDiff` (`DiffReviewDialog.cs:934-954`, `sameFile` keyed on path) — if they can be out of sync, the header/rows/comment-anchoring map mismatch files. The VM must expose which path `CurrentDiff` belongs to; render only rebuilds when they match, and shows a "loading…" header otherwise.

## Unknowns
- No profiling run — cost models are structural (source + decompiled TG 2.4.16), not measured ms. Implementers should bracket Steps 2–3 with a stopwatch UAT before/after.
- TG driver-level output cost per paint (dotnet driver write path) unverified; Step 3's win is verified structurally (per-call `VisualRoleEventArgs` alloc in the decompile).
- Whether plain `j`/`k` lag is currently discernible: #9 concluded per-cell work wasn't then the bottleneck. Steps 1–2 target the keys most likely to still lag (`m`, `e`/`E`, `s`, `/`, `n`/`N`, comment refresh); Step 3 the per-scroll paint.
