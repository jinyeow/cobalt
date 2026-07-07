# Plan — diff-review view enhancements

Follow-up UX work for the diff-review screen (`d`, `KeyScope.DiffReview`), split out
of the v1 PR into its own branch/PR. Requested during v1 UAT (2026-07-07).

## Motivation

The current diff-review file list left-truncates long paths (e.g.
`…riable.networkSecurityGroups.json`), hiding the distinguishing segment, and the
two-pane (file list + diff) layout does not degrade gracefully at small terminal
widths. Reviewers also want to switch between side-by-side and unified diffs.

## Scope

1. **Better file-name display / tree view.** Group the changed-files list by directory
   (a collapsible tree) instead of a flat, left-truncated path list, so the
   distinguishing filename is always visible. Consider reusing the aspirational
   `MoveLeft`/`MoveRight` ("collapse"/"expand") commands for tree nav.
2. **Responsive layout.** Collapse gracefully into narrow widths — e.g. hide/underlap
   the file list below a threshold, or stack panes — instead of the panes fighting for
   space.
3. **Inline vs side-by-side toggle.** A key to switch the diff pane between the current
   side-by-side view and a unified/inline view.

## Constraints / anchors

- The diff pane is a colored `IListDataSource` (ADR 0010); the diff itself is computed
  client-side (ADR 0008). Keep rendering logic in the view-model where possible so it
  stays unit-testable without Terminal.Gui (ADR 0004).
- TDD as usual; respect the multiplexer-safe driver work (ADR 0016) — verify under the
  `dotnet` driver in a multiplexer as well as a bare console.

## Status

Planning only — no implementation yet. This doc seeds the tracking PR.
