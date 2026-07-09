# 0018 — Diff review as a complete review surface

Status: Accepted · Date: 2026-07-08

## Context

After ADR 0017 (tree file list, side-by-side, responsive panes) the diff review could read
diffs but not *act* on them — replying, resolving, voting, and triaging all meant bouncing
back to the PR detail view, and large PRs were hard to navigate and read. This ADR covers the
round that makes the diff review a self-contained review surface (read → navigate → respond →
vote → triage), plus branch-policy status and PR-level comments in PR detail.

## Decision

- **One row→line map for the diff pane.** Every transform of the diff pane (unified,
  side-by-side, and now context folding) changes the mapping between a *rendered row* and the
  *original `DiffLine`*, which comment anchoring, thread viewing, navigation, and search all
  depend on. Rather than special-case each, the dialog holds a single
  `DiffRow(LineIndex, LeftIndex, RightIndex, FoldId, HiddenCount)` list and every consumer
  resolves through it (`Anchor => LineIndex ?? RightIndex ?? LeftIndex`, `-1` for a fold
  marker). The side-by-side row map is a projection of it. This keeps anchoring correct across
  all modes and makes "the cursor is on no real line" (a fold marker) an explicit, guarded
  state rather than a silent mis-anchor.

- **Context folds by default; the vim bracket family navigates.** Unchanged context collapses
  to `diff -U3`-style folds (`DiffFoldState`, pure); `e`/`E` expand one/all, and a jump to a
  hidden match expands only the fold that hides it (`ExpandContaining`). Folding applies to
  the unified view only — side-by-side shows full context. Navigation adopts the authentic vim
  motions: `]c`/`[c` for change hunks (vim's built-in diff motion), `]t`/`[t` for threads,
  `]v`/`[v` for unviewed files. This forced file navigation off bare `]`/`[` onto `]f`/`[f` —
  `KeyBindingTable.Validate()` forbids a binding being a prefix of another, and bare `]`/`[`
  are not vim motions anyway (they are prefixes awaiting a second key), so the move is more
  idiomatic, not less.

- **Pure components, thin dialog (ADR 0004 held under load).** Hunk/thread navigation
  (`DiffNavigator`), search (`DiffSearch`), folding (`DiffFold`), per-file stats and the
  unresolved filter (`PrDiffViewModel`), and the file-tree annotations (`FileTree`) are all
  UI-free and unit-tested; the dialog only wires keys to them. Per-file diff stats come from a
  background prefetch that fills a **concurrent** cache (`ConcurrentDictionary`, since the
  prefetch writes off-thread while renders read it) and is best-effort (one file's failed blob
  fetch is skipped, not fatal).

- **Branch-policy via a standalone endpoint.** `PolicyApi.GetEvaluationsAsync` queries
  `policy/evaluations` with the CodeReview artifactId
  (`vstfs:///CodeReview/CodeReviewId/{projectGuid}/{prId}`), which needs the project **GUID** —
  added as `PullRequest.ProjectId` (from the existing `repository.project.id`, no new DTO). A
  PR-level comment reuses the existing thread-create path with a null `threadContext`.

## Consequences

- The whole review loop lives in the diff view; the heavy logic stays unit-tested without a
  terminal, and comment/thread anchoring is verified across unified/side-by-side/folded modes.
- The thread overlay re-renders in place after an action: `x`/`u`/reply refetch the thread
  state (raising the view-model's `Changed`), and the overlay subscribes to re-render its body
  from the live threads (matched by the ids it opened on) rather than the opening snapshot.
- Known limitations (deferred): a line with multiple threads acts on the first (the overlay
  title names it); search highlighting is unified-only (`n`/`N` still navigate in side-by-side).
  The policy endpoint's exact shape (`7.2-preview.1`) and the `repository.project.id` population
  are runtime assumptions that degrade gracefully (policy fetch is non-fatal).
