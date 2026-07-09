# 0017 — Diff-review UX: directory tree, side-by-side toggle, responsive panes

Status: Accepted · Date: 2026-07-07

## Context

v1 UAT of the diff-review screen (`d`, `KeyScope.DiffReview`) surfaced three gaps:

- The changed-file list was a flat list of full paths, **left-truncated** to a fixed
  width (`…riable.networkSecurityGroups.json`), which hid the one segment that
  distinguishes a file.
- The two-pane layout (file list + diff) did **not degrade** at small terminal widths —
  the panes fought for space instead of collapsing.
- Reviewers wanted to switch between the unified diff and a **side-by-side** view. (The
  planning note called the existing view "side-by-side"; it was in fact **unified** — a
  single `DiffLine` column with an `old new sign` gutter, ADR 0008. This ADR adds the
  side-by-side view that did not exist.)

## Decision

All three are pure, unit-tested projections consumed by a thin dialog (ADR 0004); none add
Terminal.Gui-only code.

- **File list is a directory tree (`FileTree`).** A pure projection turns the changed
  files into a flat list of display rows — directory headers and file leaves — with a
  **row→fileIndex map** (null on headers) so navigation (`[`/`]`/`Enter`) lands on files
  and skips headers. Single-child directory chains compress (`src/Web/Api`); the basename
  always shows in full. The dialog holds only the set of collapsed directory paths and
  re-flattens; `z` collapses/expands the folder under the cursor and `Enter` on a folder
  row toggles it.

  Collapse is keyed to **`z`** (vim's fold prefix), scoped to `DiffReview`, rather than the
  Left/Right arrows: `KeyTokenizer` maps the arrows to the tokens `h`/`l` (Back/Open) — a
  deliberate, test-pinned global contract — so the arrows are not free keys, and rebinding
  them would change input semantics on every screen. `z` needs no global change.

- **Side-by-side is a pure composer (`SideBySideComposer`) reusing the existing render
  path.** `Pair` turns the unified `DiffLine` list into aligned left/right rows using the
  **same k-th-removed↔k-th-added rule** as `DiffService.FillIntraLineSpans`, so the split
  rows line up with the intra-line emphasis already computed. `Compose` lays each row out
  as a `StyledLine` — two equal-width columns (old left with the removed tint, new right
  with the added tint), padded or clipped to stay aligned — which the existing
  `DiffListDataSource` renders unchanged (ADR 0010). No new PTY-only rendering code.

  Because a paired row now maps to two original lines, the composer also returns a
  **row→(leftIndex, rightIndex) map**; the dialog resolves a comment to the correct
  original line and side through it (new side preferred, old for a deletion-only row). This
  makes explicit the row-index==line-index assumption that comment anchoring silently
  depended on. `s` toggles unified⇄side-by-side and the cursor keeps its line across the
  toggle. The token/emphasis run builder is extracted from `DiffLineStyler`
  (`AppendCodeRuns`) so both views partition code identically.

- **Responsive layout is a pure decision (`ResponsiveLayout.Compute`).** From the dialog's
  content width it returns `{ShowFileList, FileListWidth, AllowSideBySide}`: below a width
  threshold the file list is hidden so the diff pane spans the full row; the file-list
  width scales with the terminal (clamped 20–40); and side-by-side is disallowed — and an
  active side-by-side falls back to unified — when the diff pane is too narrow for two
  columns. The dialog applies the result and re-applies it on resize via the same
  `ViewportChanged` hook the width-aware lists use. The side-by-side column width derives
  from the **computed** diff-content width, not the (possibly stale) pane viewport, so
  columns size correctly the same frame the file list toggles.

## Consequences

- The heavy logic — tree flattening, diff pairing, layout thresholds — is unit-tested
  without a terminal (ADR 0004); the dialog wiring is exercised headlessly by driving keys.
- Side-by-side reuses the ADR 0010 `DiffListDataSource`, so it inherits the same
  cell-level color path with no new PTY surface; the only new render risk is column-tint
  fill, mitigated by full-width padding runs.
- **Side-by-side clips long lines to the column width** (no per-column horizontal scroll);
  unified mode remains for reading full-width lines. Consistent with the wide-character
  clipping already documented in ADR 0010.
