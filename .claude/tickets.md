# Tickets: lazygit-inspired TUI redesign

Tracer-bullet slices for `.claude/specs/lazygit-inspired-tui-redesign.md` — adopting
the lazygit interaction grammar (context keybar, real tabs, list + live preview
workspace, executable help, verb menus, transparency) in independently shippable
stages.

Work the **frontier**: any ticket whose blockers are all done. For a purely linear
chain that means top to bottom.

## 1. Always-visible context keybar

**What to build:** a bottom keybar line, always visible, showing the highest-value
keys for the focused context (like lazygit's bottom line / gitui's footer). Generated
from the live key-binding table for the active scope so it can never drift from
behaviour; a pure formatter prioritizes and fits entries to the terminal width with
an ellipsis hint pointing at `?` when truncated. Updates when the section (and later,
the focused pane) changes.

**Blocked by:** None — can start immediately.

- [ ] A keybar renders at the bottom of the shell in both sections, listing key → action pairs for the current scope
- [ ] Content comes from the binding table (a new binding appears without touching the keybar)
- [ ] Entries are prioritized and truncated to width with a trailing `? more` hint; never wraps
- [ ] Formatter is a pure function covered by table-driven unit tests (scope × width)

## 2. Showcmd: pending sequence and count indicator

**What to build:** vim's showcmd for cobalt — while a multi-key sequence or count
prefix is armed (`5`, `g`, `]` …), the pending tokens render at the right edge of the
status line; `Esc` or completion clears them. The key router exposes its pending
state as observable data; nothing else re-implements sequencing.

**Blocked by:** Always-visible context keybar (shares the chrome row layout).

- [ ] Typing `5` then `j` shows `5` (then clears) in the status area; `g` shows `g` until the chord resolves
- [ ] `Esc` clears the indicator along with the pending sequence
- [ ] Router pending-state exposure is unit-tested; the status-line composition is unit-tested

## 3. Real tab strip with jump hints and `[`/`]` sub-tabs

**What to build:** replace the fake text tab label with a rendered tab strip: section
tabs carry their jump chords (`g1`/`g2`), and in the PR section the four sub-tabs
(review queue / team / mine / active) render as a visible second-level tab row with
the active one highlighted and counts shown. `[` / `]` switch PR sub-tabs (lazygit's
panel-tab keys), freeing `Tab` for pane focus later; `Tab` keeps working as an alias
until the preview workspace lands.

**Blocked by:** None — can start immediately.

- [ ] Section tabs show jump hints; active section is visually distinct in both themes
- [ ] PR sub-tabs are visible as a tab row with the active tab highlighted (no longer only header prose)
- [ ] `[`/`]` cycle PR sub-tabs; `Tab`/`S-Tab` still work; work-item section unaffected
- [ ] Tab-strip formatting is pure and unit-tested; sub-tab key routing covered at router/view-model level

## 4. PR list + live preview workspace (flagship)

**What to build:** in the PR section, the content area splits into the PR list (left)
and a preview panel (right) that renders the selected PR's detail body — title,
reviewers/votes, description, threads — updating as the cursor moves. Preview fetches
are async and cancel-superseded: navigation never blocks, stale renders are
discarded, and a fetching placeholder shows while loading. `Tab`/`C-h`/`C-l` move
focus between list and preview (focused pane visually marked); vim scroll keys act on
the focused pane; `Enter` still opens the full detail modal for actions. Below a
width threshold the preview collapses and today's full-width list returns
(generalizing the diff dialog's pure responsive-layout calculator).

**Blocked by:** Real tab strip with jump hints and `[`/`]` sub-tabs (releases `Tab`
from sub-tab duty).

- [ ] Moving the cursor in the PR list updates the preview with that PR's rendered detail
- [ ] Rapid j/j/j never blocks the cursor; only the final selection's preview renders; placeholder shown while fetching
- [ ] `Tab`/`C-h`/`C-l` cycle focus; scroll keys scroll whichever pane has focus; keybar reflects the focused pane
- [ ] Narrow terminals collapse the preview automatically; behaviour matches the pure layout function's decision
- [ ] Enter/detail modal, votes, and all existing PR list behaviour unchanged
- [ ] Workspace state (focus, selection→preview, cancellation) lives in view-models with fake-store unit tests; key delivery has a headless view test

## 5. Work-item list + live preview

**What to build:** the same workspace grammar for work items: list left, preview
right (fields, description, recent comments), async cancel-superseded loads,
shared focus/zoom/responsive behaviour — reusing the workspace machinery from the PR
slice, not duplicating it.

**Blocked by:** PR list + live preview workspace.

- [ ] Cursor movement previews work items with the same async/cancellation guarantees
- [ ] Focus cycling, responsive collapse, and keybar integration behave identically to the PR workspace
- [ ] Existing work-item actions (comment/state/assign/tags) and detail modal unchanged

## 6. Reusable menu component + executable `?` help

**What to build:** one menu popup component — j/k navigation, `/` filter, Enter
executes, Esc dismisses, its own key scope — and rebuild `?` help on it: the current
context's bindings become menu rows (key hint + description) that execute on Enter.
The cheatsheet becomes a command palette (lazygit's `?` menu), still generated from
the binding table.

**Blocked by:** None — can start immediately.

- [ ] `?` opens a menu of the active context's bindings; Enter runs the selected command; Esc closes
- [ ] `/` filters menu rows incrementally; filtered Enter still executes
- [ ] Menu is data-driven (label, key hint, command) with its own key scope; view-model unit tests plus one headless key-delivery test
- [ ] Modal dialogs' `?` continues to show only keys that work there

## 7. Vote and state verb menus

**What to build:** transient-style option menus on the shared menu component: `v`
opens a vote menu (approve / approve with suggestions / wait for author / reject /
reset) wherever voting works today, and `s` on a work item opens a state menu listing
the item type's actual states. Selection applies the action and reports to the
message bar.

**Blocked by:** Reusable menu component + executable `?` help.

- [ ] `v` shows the five vote options with key hints; choosing one casts the vote (replaces the current vote prompt)
- [ ] `s` lists the selected work item's valid states fetched for its type; choosing one transitions the item
- [ ] Menu content builders are pure and unit-tested; existing vote/state behaviour reachable and unchanged in effect

## 8. Preview zoom (screen modes)

**What to build:** lazygit's screen-mode cycle for the workspace: `+` grows the
focused pane (normal → half → full), `_` shrinks; full-screen preview turns the
workspace into a reading surface and back without opening a modal.

**Blocked by:** PR list + live preview workspace.

- [ ] `+`/`_` cycle pane weighting; full mode hides the other pane; the cycle returns to normal
- [ ] Mode survives selection changes and section switches sensibly (per-section reset is acceptable)
- [ ] Layout decision is a pure function with unit tests; keybar advertises the keys

## 9. Saved-query sections as tabs

**What to build:** gh-dash-style user-defined sections in `config.toml` — named
saved queries (work-item WIQL or PR filter parameters) that appear as additional
sub-tabs after the built-in ones and load their query when activated. Invalid
sections surface a message and are skipped, never fatal.

**Blocked by:** Real tab strip with jump hints and `[`/`]` sub-tabs.

- [ ] A configured section appears as a tab, loads its query's rows, and cycles with `[`/`]` like built-ins
- [ ] Work-item and PR section configs both supported; zero-config behaviour identical to today
- [ ] Config parsing/validation is unit-tested including malformed sections (skipped + messaged)

## 10. API operations log

**What to build:** lazygit's command-log transparency for ADO: the client layer
emits an operation event per REST call (operation name, route shape, duration,
outcome — never secrets), buffered like the message log; `:log` opens the operations
history. Groundwork for a future toggleable log panel.

**Blocked by:** None — can start immediately.

- [ ] Every ADO call produces one log entry with name, route shape, duration, and outcome
- [ ] `:log` shows the buffered history newest-last; `:messages` unchanged
- [ ] Emission is covered by client-layer unit tests; no tokens/headers/bodies ever logged

## 11. Per-panel loading indicators

**What to build:** a loading state per panel: the list header and preview header
show a spinner/`loading…` marker while their fetch is in flight, cleared on arrival
or failure (failures keep routing to the message bar). The rest of the UI stays
interactive throughout.

**Blocked by:** PR list + live preview workspace.

- [ ] List reload and preview fetch each show their own loading marker; neither blocks input
- [ ] Marker clears on success and on expected failure; failure text still reaches the message bar
- [ ] Loading state lives in view-models and is unit-tested with delayed fake stores
