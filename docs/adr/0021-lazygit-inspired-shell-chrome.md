# 0021 — Lazygit-inspired shell chrome: context keybar, showcmd, real tab strip

Status: Accepted · Date: 2026-07-17

## Context

A survey of best-in-class TUIs (lazygit primarily; gh-dash, tig, gitui, k9s,
magit/transient) against cobalt's shell surfaced three discoverability gaps —
this ADR covers the first stage of that larger redesign (keybar, showcmd, tab
strips; later stages: list + live-preview workspace, executable help menus,
verb menus, saved-query sections):

- **Keys were invisible.** Nothing on screen said what you can press in the current
  context; `?` was the only route and it interrupts the flow. lazygit/gitui keep a
  context-sensitive key line permanently on the bottom row, and it is the single
  biggest reason those tools feel learnable.
- **Multi-key state was invisible.** The vim layer supports count prefixes (`5j`) and
  chords (`g…`, `y…`), but an armed count or pending sequence gave no feedback — vim
  itself solves this with `showcmd`.
- **The tabs were prose, not tabs.** The section strip was a hand-built string in the
  shell, and the PR list's four sub-tabs existed only as words in a header sentence
  (`pull requests · team (7)   [Tab] switch`).

## Decision

- **A bottom keybar, generated from the live `KeyBindingTable`.** `KeybarFormatter`
  (pure) renders the focused scope's bindings as ` key:label` pairs: a curated
  priority list puts contextual verbs first (the lazygit lesson — the bar teaches
  what you can do to the *selected thing*), commands without a curated label fall
  back to the shared `HelpText` vocabulary so **new bindings surface without
  touching the formatter**, aliases collapse to the densest key (`o` not `Enter`),
  reverse/scroll noise is suppressed, and the result is fitted to the terminal width
  with `?:help` guaranteed as the last entry. Deriving from the binding table is the
  property that matters: the bar can never lie (same reason `HelpText` reads the
  table).
- **Showcmd on the status row.** `KeymapRouter` exposes `PendingDisplay` (armed count
  + pending tokens, as typed) — the router already owns sequencing state, so nothing
  re-implements it. `StatusLineComposer` (pure) right-aligns it on the status row;
  the shell re-renders the row after every fed key.
- **Real tab strips.** `TabStripFormatter` (pure) renders the section tabs with
  their jump chords (`[g1:Work Items]  g2:Pull Requests `) and the PR sub-tabs as a
  visible tab row in cycle order with the active tab bracketed + counted
  (` review queue │ [team 7] │ mine │ active`). `[` / `]` now cycle the PR sub-tabs
  (lazygit's panel-tab keys), scoped to `PullRequestList`; `Tab`/`S-Tab` remain as
  aliases until the preview workspace (ADR 0022, stage B) claims `Tab` for pane focus.
- **Chrome layout.** The bottom of the shell is now status / message (palette
  overlays it) / keybar, with the keybar owning the last row like lazygit's bottom
  line. The chrome re-fits on terminal resize (same width-change guard the list
  screens use).

## Consequences

- Three new pure formatters (`KeybarFormatter`, `StatusLineComposer`,
  `TabStripFormatter`) are fully unit-tested; shell integration is covered by
  headless `NewKeyDownEvent` tests (`ShellChromeTests`). No view-model gained a
  Terminal.Gui type (ADR 0004 holds).
- The keybar shows the *shell's* scopes today. When the stage-B workspace adds pane
  focus, the bar must follow the focused pane's scope — that seam (a `KeyScope`
  parameter) already exists.
- `[`/`]` are now taken in `PullRequestList`; a future work-item sub-tab row should
  reuse the same keys for consistency.
- One row of vertical space moved from content to chrome. Accepted: lazygit pays the
  same row and it is the highest-value line on its screen. If height ever matters
  more (very short panes), a `showBottomLine`-style config toggle is the escape
  hatch — deferred until asked for.
