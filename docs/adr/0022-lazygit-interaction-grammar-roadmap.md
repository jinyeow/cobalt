# 0022 — Adopt the lazygit interaction grammar (redesign roadmap)

Status: Accepted · Date: 2026-07-17

## Context

Cobalt's shell predates any deliberate interaction design: a single list swapped in
place, every deeper surface a stacked modal, keys discoverable only via `?`. A
survey of best-in-class TUIs — lazygit foremost (multi-panel layout, bottom keybar,
executable `?` menu, panel tabs, screen modes, command log, and Jesse Duffield's
stated philosophy: *state always visible over query-response; density over
whitespace; speed as a cultural contract; transparency over magic*), plus gh-dash
(saved-search sections + preview pane, the closest analogue for a PR/work-item
dashboard), tig (parent/child split), gitui (context footer), magit/transient (verb
menus), and k9s (command mode) — identified a coherent grammar cobalt should adopt
rather than invent its own.

This ADR records the destination and its staging; each stage lands with its own
ADR as it ships. Work items for the stages are tracked as GitHub issues.

## Decision

Adopt the lazygit interaction grammar in five independently shippable stages, in
this order:

- **A — Discoverability chrome** (shipped; ADR 0021): context keybar generated from
  the live binding table, vim-showcmd pending-key indicator, real tab strips with
  jump chords, `[`/`]` as the panel-tab keys.
- **B — List + live-preview workspace** (the flagship; concrete design: ADR 0024):
  the content area splits
  into list (left) and a preview panel (right) that re-renders the selected item's
  detail on every cursor move. Constraints that are part of the decision: preview
  fetches are **async and cancel-superseded** (navigation never blocks; stale
  renders discarded); the preview **reuses the existing detail view-models and
  renderers** (no second detail formatter); **focus is an explicit workspace
  concept** owned by a view-model (driving the existing `FocusLeft`/`FocusRight`
  commands and `Tab`), not inferred from Terminal.Gui focus; responsive collapse
  generalizes the diff dialog's pure layout calculator; `Enter` still opens the
  detail modal for actions. PRs first, work items second, sharing the machinery.
- **C — Executable help and verb menus**: one reusable menu component (own key
  scope: j/k, `/` filter, Enter executes, Esc dismisses) backs an executable `?`
  cheatsheet and transient-style option menus for enum-valued verbs (PR vote,
  work-item state). Menu contents are data (label, key hint, command).
- **D — Saved-query sections**: gh-dash-style user-defined sections in
  `config.toml` (work-item WIQL / PR filter parameters) materialize as additional
  sub-tabs after the built-ins; invalid sections are messaged and skipped, never
  fatal. The user's workflow becomes the information architecture.
- **E — Transparency and polish**: an API operations log (operation name, route
  shape, duration, outcome — never tokens/secrets) buffered like the message log
  behind `:log`; per-panel loading indicators; a lazygit-style screen-mode zoom
  (`+`/`_`) for the workspace panes.

Cross-cutting rules the stages must respect: all new behaviour lives in pure
formatters/view-models (ADR 0004) so it is headless-testable; keys and hints derive
from `KeyBindingTable` (ADR 0007) so UI can never drift from behaviour; digits stay
reserved for count prefixes (panel jumps stay on `g`-chords).

## Out of scope

Diff-review redesign (it already implements this grammar; only its layout
calculator is generalized), user-remappable keybindings, mouse support, range
select/bulk ops, undo, `:context` live reconnection, and board/kanban views —
each a separate decision when its time comes.

## Consequences

- Stages ship alone and in order of value; every stage leaves the app releasable.
- Stage B changes the product's core feel and carries the async/cancellation
  complexity; its constraints above are the guardrails against a laggy or
  flickering preview.
- The staged plan replaces a planning spec that previously lived outside the docs
  tree; this ADR is now the single persisted record of the direction.
