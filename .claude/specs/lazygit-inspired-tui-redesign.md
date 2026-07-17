# Lazygit-inspired TUI redesign

Source research: lazygit (primary — layout, discoverability, philosophy), gh-dash
(saved-search sections, preview pane), tig (parent/child split), gitui (context help
footer), magit/transient + neogit (verb menus), k9s (command mode), yazi (async
previews). Current-state map: cobalt is a single-window app — a text-only "tab strip",
one list screen swapped in place, and every deeper surface (detail → diff → thread) a
stacked modal dialog. The only real multi-pane surface is the diff review dialog.

## Problem Statement

As a cobalt user I can browse and act on work items and PRs, but the experience
diverges from the best-in-class TUIs I use daily (lazygit):

1. **I can't see anything without committing to it.** The list is the whole screen;
   to read a work item or PR I must Enter into a modal, then `q` back out. lazygit's
   core loop — cursor moves, preview updates, state always visible — doesn't exist.
2. **The keys are invisible.** There is no bottom keybar showing what I can press in
   the current context, no indication of a pending multi-key sequence or count
   prefix (`5j`, `g…`), and `?` opens a plain text dump I can only read, not run.
3. **The chrome doesn't look or behave like panels.** The tab strip is a plain text
   label; the PR list's four sub-tabs exist only as words in a header line; there is
   no panel numbering, no focus cycling (`C-h`/`C-l` are bound but dead), no zoom.
4. **Verb keys act blind.** Enum-valued actions (vote, work-item state) deserve
   transient-style menus listing the options with keys, not memorized single keys
   plus a message-bar hint when they miss.
5. **The API is opaque.** ADO calls happen invisibly; `:messages` shows outcomes,
   not operations — lazygit's command log proves transparency builds trust.

## Solution

Adopt the lazygit interaction grammar in stages, keeping cobalt's existing modal
drill-ins working underneath so every stage ships alone:

- **Stage A — discoverability chrome**: an always-visible bottom **keybar** generated
  from the live key-binding table for the focused context; a vim-style **pending
  sequence / count indicator** ("showcmd") in the status bar; a real **tab strip**
  with panel-jump numbers for sections and a visible sub-tab row for the PR list's
  four tabs, switchable with `[`/`]` like lazygit panel tabs.
- **Stage B — list + live preview workspace**: split the main content area into the
  list (left) and a **preview main panel** (right) that re-renders the selected
  item's detail on every cursor move — asynchronously, never blocking navigation.
  Focus moves with `Tab`/`C-h`/`C-l`; `Enter` still drills into the full detail
  modal; the preview collapses responsively on narrow terminals (the diff dialog's
  responsive-layout pattern, generalized).
- **Stage C — executable help & verb menus**: `?` becomes a filterable **menu** whose
  rows execute on Enter (cheatsheet = command palette, generated from the binding
  table so it cannot drift); vote and work-item state get transient-style **option
  menus** built on one reusable menu component with its own key scope.
- **Stage D — dashboard sections**: gh-dash-style user-defined saved-query sections
  in `config.toml` become additional tabs (e.g. "needs my review", per-project
  slices) — the user's workflow becomes the information architecture.
- **Stage E — transparency & polish**: an API operations log surface (which REST
  calls ran, elevating the existing message history), per-panel loading indicators,
  and a preview/panel **zoom** cycle (normal → half → full).

## User Stories

1. As a cobalt user, I want a bottom keybar that always shows the keys available in the focused context, so that I discover functionality without opening help.
2. As a vim user, I want my pending key sequence and count prefix displayed (like vim's showcmd), so that I know `5` + `j` is armed and `Esc` clears it.
3. As a cobalt user, I want the section tabs and PR sub-tabs rendered as a real tab strip with jump numbers, so that I always know where I am and can jump directly.
4. As a PR reviewer, I want to move the cursor down the PR list and read each PR's description, reviewers, and threads in a preview panel beside the list, so that I triage a review queue without opening and closing a modal per PR.
5. As a work-item owner, I want the same live preview for work items, so that I can scan my sprint items' descriptions and latest comments in one pass.
6. As a keyboard user, I want `Tab`/`C-h`/`C-l` to move focus between the list and the preview, so that I can scroll a long description without leaving the workspace.
7. As a user on a narrow terminal (or split pane), I want the preview to collapse automatically and everything to remain usable, so that cobalt works inside a multiplexer pane.
8. As a new user, I want `?` to open a filterable menu of the current context's bindings that I can execute directly, so that the cheatsheet doubles as a command palette.
9. As a PR reviewer, I want `v` to open a vote menu (approve / approve with suggestions / wait for author / reject / reset), so that I never memorize vote enum keys.
10. As a work-item owner, I want `s` to open a state menu listing the item type's actual states, so that I pick rather than type.
11. As a power user, I want to define saved-query sections in config that appear as tabs, so that my actual workflow (my PRs, needs my review, current sprint) is the navigation.
12. As a sceptical user, I want to see which ADO API operations cobalt runs, so that I trust and can debug what it does on my behalf.
13. As a user waiting on a slow org, I want a loading indicator on the panel that is fetching while the rest stays interactive, so that latency never feels like a hang.
14. As a focused reviewer, I want to zoom the preview panel (normal/half/full), so that a long diff or description can take the whole screen and come back.

## Implementation Decisions

- **The keybar, showcmd, tab strip, and menus are all pure view-model/formatter
  logic**; the shell views only bind strings/state to widgets. This keeps every new
  behaviour headless-testable in CI (the house MVVM rule).
- **The keybar derives from the live `KeyBindingTable` for the active scope** —
  the same single source the `?` help already uses — so hints can never drift from
  behaviour. It shows a curated, prioritized subset that fits the width (a pure
  fit-to-width formatter), not every binding.
- **Showcmd state comes from the key router**: the router already knows pending
  tokens and count; expose that as observable state rather than duplicating
  sequencing logic anywhere else.
- **The preview panel reuses the existing detail view-models and their renderers**
  (work-item body, PR body) — no second detail formatter. The modal detail dialogs
  stay; `Enter` opens them for actions until Stage C folds actions into the
  workspace.
- **Preview loads are asynchronous and cancel-superseded**: cursor movement cancels
  the in-flight preview fetch (the comment-count enricher's cancellation pattern);
  the list never waits on the preview. A generation token discards stale renders.
- **Responsive collapse generalizes the diff dialog's pure layout calculator**: one
  pure function decides preview visibility/width from terminal width; below the
  threshold the workspace degrades to today's full-width list.
- **Focus is an explicit workspace concept** (list pane vs preview pane), owned by a
  view-model, not inferred from Terminal.Gui focus; the existing dead
  `FocusLeft`/`FocusRight` commands and `Tab` drive it. Key dispatch branches on it
  (the diff dialog's pane pattern, promoted to the shell).
- **One reusable menu component** (its own key scope: j/k navigation, `/` filter,
  Enter execute, Esc dismiss) backs the executable `?` help, the vote menu, and the
  state menu. Menu contents are data (label, key hint, command), so each concrete
  menu is a pure list builder.
- **`[` / `]` switch sub-tabs** in list scopes (lazygit's panel-tab keys); `Tab`
  moves pane focus once the preview exists (its current PR sub-tab duty migrates to
  `[`/`]`). Digits stay reserved for count prefixes — panel jumps stay on `g`-chords
  (`g1`/`g2`), with the tab strip displaying those jump hints.
- **Saved-query sections are config data** validated at load: each section has a
  name and a query (work-item WIQL or PR filter parameters). Sections materialize
  as additional tabs after the built-in ones; invalid sections surface in the
  message bar and are skipped, never fatal.
- **The API log is an event stream from the ADO client layer** (operation name,
  route shape, duration, outcome — never tokens/secrets), buffered like the message
  log and shown via `:log` (and later a toggleable panel). Expected-failure policy
  is unchanged.
- **New architectural decisions get an ADR** (next number in sequence), and
  README/CHANGELOG update with each shipped stage — per house convention.

## Testing Decisions

- Test external behaviour at existing seams, never widget internals: formatters and
  view-models get plain unit tests (prior art: router, responsive-layout, and
  row-formatter test suites).
- The keybar/showcmd/tab-strip formatters are pure string builders — table-driven
  unit tests over scope × width × pending-state.
- Workspace focus, preview-follows-selection, cancellation-on-move, and menu
  execution are view-model tests with fake stores (prior art: PR list view-model
  tests with fake adapters).
- Key delivery through real Terminal.Gui routing gets headless view-level tests
  driving `NewKeyDownEvent` (prior art: the detail-dialog key-delivery suite).
- Async tests pass the ambient test cancellation token (xunit v3 house rule).
- The full suite must stay green per stage; each stage is independently shippable.

## Out of Scope

- **Diff review dialog changes** — it already implements the panel/pane grammar;
  it stays a full-screen surface. Only its layout calculator is generalized.
- **User-remappable keybindings** — the binding table remains the single seam for a
  future remap config; this redesign does not implement remapping.
- **Mouse support**, range-select/bulk operations, and undo — worthy follow-ups,
  separate specs.
- **`:context` live reconnection** — a known limitation, orthogonal to UI layout.
- **Work-item board/kanban views** — this redesign improves the list+detail
  grammar; new view types are future work.

## Further Notes

- lazygit's philosophy anchors the taste calls: state visible at all times over
  query-response; density over whitespace; speed as a cultural contract (never
  block the cursor on I/O); transparency over magic (the command log).
- Stage A alone is a large UX win for near-zero risk and should ship first; Stage B
  changes the product's feel and is the flagship; Stages C–E are independent
  follow-ons once the workspace exists.
- gh-dash is the closest domain analogue (PR dashboard with saved-search sections +
  preview pane) and validates Stage B/D's fit for the PR-review workflow.
