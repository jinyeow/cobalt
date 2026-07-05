# 0010 — Color the diff pane through a custom `IListDataSource`, styled by a pure run model

Status: Accepted · Date: 2026-07-05

## Context

PR review should highlight the exact words that changed on a modified line, color
code tokens by language, and diff a renamed file old-path→new-path instead of
rendering it as a whole-file add. The diff pane is a Terminal.Gui `ListView` whose
rows were plain strings (`DiffReviewDialog.FormatLine`), so it had no way to color
runs within a row.

Probed against Terminal.Gui 2.4.16 (reflection + a live pyte PTY, cell-level color
assertions):

- `Terminal.Gui` v2 has **no rich-text ListView**. `ListView.RowRender`
  (`ListViewRowEventArgs`) sets a whole-row attribute only — it cannot color runs.
  Markup-in-string (the v1 `[color]` idea) does not exist.
- The supported seam is `IListDataSource.Render(ListView, selected, item, col, row,
  width, viewportX)`. Inside it, `listView.Move(col,row)` then alternating
  `SetAttribute(Attribute)` + `AddStr(string)` produces per-cell colored runs within
  one row; `GetAttributeForRole(VisualRole.Code*)` yields the theme's token colors.
  This is PTY-proven on 2.4.16.
- The theme's `VisualRole.Code*` foregrounds are only distinct in the **`Base`**
  scheme (Keyword `#569CD6`, Identifier `#9CDCFE`, String `#CE9178`, …). The
  `Dialog` scheme a modal inherits collapses every Code role to its `Normal`
  foreground, so a diff pane left on the Dialog scheme shows all tokens one color.

## Decision

- **Keep the diff pane a `ListView`; replace its source with a custom
  `DiffListDataSource : IListDataSource`.** This keeps ListView's scrolling,
  selection, viewport, and focus for free (a custom `View` would re-implement all of
  it), and beats `RowRender` which is whole-row only. `Render` draws each row as a
  sequence of styled runs, slicing by `viewportX`, and pads the remainder of the row
  in the line-kind background (full-width GitHub-style tint). The selected row is
  drawn wholesale in `VisualRole.Focus` so the cursor bar stays unmistakable.

- **Foreground = syntax token role; background = diff state — they layer, not
  fight.** A pure `DiffLineStyler.Compose` turns a `DiffLine` + its syntax tokens +
  a thread flag into a `StyledLine`: the display string (byte-for-byte the legacy
  `FormatLine` output, asserted as a regression) plus a gapless, ordered run
  partition. Runs are the syntax tokens, split at every `ChangedSpans` boundary so
  emphasis lands exactly on the changed words while each run keeps its token kind.
  The adapter maps one `RunStyle` → one `Attribute` with no logic: foreground from
  `GetAttributeForRole(Code*)`, background from the diff state (Context → Normal;
  Added `#123a12` / emphasis `#1e6b1e`; Removed `#3a1212` / emphasis `#6b2020`);
  the gutter sign is `ColorName16.BrightGreen`/`BrightRed`. Backgrounds are derived
  from `GetAttributeForRole`, never white/black literals, so default terminal
  themes still work; RGB degrades to the nearest palette color on non-truecolor
  terminals, where the sign column and emphasis tint still carry the signal.
  Everything except the attribute mapping is a pure Core class, unit-tested per
  ADR 0004.

- **The diff pane runs on the `Base` scheme.** `_diffPane.SchemeName = "Base"` so
  the `VisualRole.Code*` token foregrounds actually resolve; on the inherited Dialog
  scheme they are indistinguishable.

- **Syntax highlighting is strictly line-local.** A single-pass `SyntaxTokenizer`
  parameterized per language (`LanguageDetector`: C#, JS/TS, JSON, Python; else a
  plain fallback) with no state carried across lines. The interior of a `/* … */`
  block comment or a multi-line/verbatim string tokenizes as code — a deliberate
  cut (correct cross-line state needs two state streams threaded through interleaved
  old/new diff lines). Documented limitation; fast follow.

- **Intra-line spans come from a word-level DiffPlex pass with a similarity guard.**
  `IntraLineDiff.Compute` word-chunks the two sides and maps changed piece-blocks to
  char spans via prefix sums; a `DiffService.Unified` post-pass pairs each run of
  Removed lines with the following run of Added lines and fills `ChangedSpans`. If
  more than 60% of either side changed, spans are dropped — a total rewrite renders
  as a plain add/remove, not confetti.

- **Renames diff base-at-`sourceServerItem`** (ADR 0008's client-side-diff model).
  `PrChangeEntryDto` now carries `sourceServerItem`/`originalPath`; `FileChange`
  gains an optional `OriginalPath`; `PrDiffViewModel` fetches the base blob at
  `OriginalPath ?? Path` (source blob stays at `Path`). A renamed file with one
  edited line now diffs `+1 -1` instead of a whole-file `+N -0`.

## Consequences

- The optional positional parameters (`DiffLine.ChangedSpans`,
  `FileChange.OriginalPath`) keep every existing construction site compiling.

- **Two small dialog-wiring fixes were required by the PTY smoke, and both are real
  bugs the old code shared:**
  - **ListView type-ahead swallowed the vim command keys.** `ListView`'s
    `KeystrokeNavigator` (CollectionNavigator) consumes printable letters for
    type-ahead search before they bubble to the dialog's `KeyDown`, so `q`, `c`,
    `[`, `]` (and the new `j`/`k`) were eaten once a list had focus. The dialog now
    sets `KeystrokeNavigator = null` on both `ListView`s — this dialog drives on
    single-key commands, not list search.
  - `j`/`k` are added to the dialog `KeyDown` (forwarding to `MoveDown`/`MoveUp` on
    the focused pane), because vim keys are bound only in the main shell's
    `KeymapRouter`, which modal dialogs never see.

- The colored `DiffListDataSource` + attribute mapping + dialog wiring are the only
  code touching `Move`/`SetAttribute`/`AddStr`/`GetAttributeForRole`, so they are
  PTY-verified (pyte cell-level fg/bg), not unit-tested. The smoke drives the real
  `DiffReviewDialog` over an in-memory `IPrDiffSource` and asserts, per cell: the
  `+` line carries the added background (`123a12`) with `cost` in emphasis
  (`1e6b1e`); the `-` line carries removed (`3a1212`) with `price` in emphasis
  (`6b2020`); keyword `var` (`569cd6`) vs identifier `total` (`9cdcfe`) differ; the
  `●` gutter marker shows on the thread line; `]` moves to the renamed file whose
  header reads `+1 -1` (not `+10 -0`); `j`/`k` move the selection; `q` closes the
  dialog. As with ADR 0009, the harness must answer the terminal's size/DA/color
  queries or Terminal.Gui draws empty frames — a property of the emulator, not the
  code.
