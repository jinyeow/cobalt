# 0025 — Reusable menu component, and `?` as an executable menu

Status: Accepted · Date: 2026-07-28

## Context

ADR 0022 stage C names a popup menu as the grammar's third stage: a list of rows the user
can navigate, filter, and **run**, with its own key scope. Two surfaces want it. The `?`
help was a static string cheatsheet — it told you `s` changes state but you still had to
close it and press `s`. The vote and work-item-state pickers (issue #26) currently go
through an injected `Func<string, IReadOnlyList<string>, int?>` picker — one seam each in
`PrActions` and `WorkItemActions` — which defaults to a `MessageBox` with no filtering and
no vim keys.

Building the menu for `?` alone would produce a help-shaped thing #26 could not reuse, so
the component is generic from the start and `?` is its first consumer.

## Decision

### Rows carry a value, not a delegate

`ViewModels/MenuOption<T>(string Label, string KeyHint, T Value)` is the whole data model.
`T` is the verb domain: `AppCommand` for `?`, a vote or state value for #26. A row carries
the value the host acts on rather than an `Action`, so row builders stay pure and unit
testable and the "view-model decides, shell performs" split of ADR 0007 survives — the
alternative (a delegate payload) lets a builder embed side effects, and the alternative to
*that* (parallel arrays indexed by row) reintroduces exactly the index bookkeeping across
filtering that the view-model exists to prevent.

### `MenuViewModel<T>` owns everything except key delivery

Terminal.Gui-free (ADR 0004): visible rows, the filter, the clamped selection, and
`FormatRows(width)`. The filter is an **order-preserving** case-insensitive subsequence
match over `"<hint> <label>"`, sharing the matcher (`ViewModels/FuzzyFilter`) extracted
from `PaletteSuggestionsViewModel`. The palette's prefix-first *reordering* is deliberately
not reused: help rows are anchored by their key hints, and re-ranking on every keystroke
makes the hints jump. Setting a filter puts the highlight back on the top match, so Enter
straight after typing runs the row the user is looking at — and `j`/`k` still move within
the filtered rows, so `j`-then-Enter never lies.

### `KeyScope.Menu` with zero bindings of its own

Everything the menu needs is already Global: `j`/`k`/`gg`/`G`/`C-d`/`C-u` (movement),
`Enter`/`o`/`l` → Open (run the row), `q`/`h` → Back and `Esc` (dismiss), `/` → FilterStart.
The scope exists so routing is menu-addressed, so `[keys.menu]` is a valid remap section on
day one (ADR 0023's `ResolveScope` walks the enum), and so a future menu-only key has a
home. Adding duplicate scoped bindings would only add prefix-conflict surface to `Validate`.

Consequence, accepted: the Global `h` dismisses and `l` runs the highlighted row inside a
menu, consistent with every other screen.

### `Screens/MenuDialog` swallows every matched command

Unlike the four detail dialogs — where `DialogKeyRouter` deliberately lets a
matched-but-unhandled command fall through to the widget's native behaviour — the menu's
`Dispatch` returns `true` for **every** match. A menu has no native behaviour worth falling
through to, and an unhandled key bubbles on to the shell, where `?` would open a second
menu while the first is still up. This is a `Dispatch` return value, not a change to the
shared router.

It guards the key path only: the Help **row** is offered and, when chosen, dispatches back
into the shell's `ShowHelp`. That is deliberate and safe — the pick is acted on after the
popup's run loop has stopped, so the second menu opens *after* the first has closed rather
than on top of it (`ShellHelpMenuTests.Choosing_The_Help_Row_Reopens_The_Menu_Sequentially`).

The filter bar is a `TextField` on the bottom row, hidden until `/`. While it has focus the
dialog-level router stands down (ADR 0014's search-bar guard): printable runes belong to the
field, and the control chords that still bubble to the `Dialog` — `C-u`, `C-d` — would
otherwise scroll the list underneath it. Verified, not assumed: with the guard removed,
`C-u` while filtering moves the selection
(`MenuDialogKeyDeliveryTests.CtrlU_While_Filtering_Does_Not_Scroll_The_List_Underneath`).
`Esc` in the field cancels the filter only; a second `Esc` closes the menu. Focus leaving
the field by any other route (a click on a row) cancels the filter too, so the bar is never
left visible with stale text while the guard has already stood down.

### The choice is executed after the run loop stops

`MenuDialog.Run` **returns** the chosen row; the host acts on it once the popup's run loop
has stopped. Executing from inside the menu's loop would nest run loops the moment a chosen
verb opens its own dialog. This is the established close-then-act pattern (the command
palette, `PrActions`).

### One row builder behind both help surfaces

`HelpText.MenuFor` / `MenuForDialog` build the rows; `For` / `ForDialog` re-emit the string
cheatsheet from them. Every suppression rule — the preview-dependent `Tab` wording (#48),
the dead-dialog-globals filter (M3), first-binding-wins alias collapse — lives once, in the
row builder. `HelpTextTests` pins the string overlay against the pre-menu implementation's
own output (golden rows per scope and preview state) and pins the rows against that same
overlay, so a change in the shared builder has to break one of them. The
shell's `?` (and `:help`) now open the menu; a chosen row goes through the shell's existing
`Dispatch`, so a row that is not available in the current context lands in its existing
"not available here" message.

## Consequences

- The four modal dialogs' `?` **keeps** the `TextDialog` cheatsheet for now. Issue #20's
  requirement is that dialog help only advertise keys that work there, which commit 6
  guarantees for both representations from the one row builder. Converting them means
  executing a dialog verb from a nested modal across four dialogs; it is a clean follow-up
  (`MenuForDialog` rows, `Dispatch(row.Value, null)` on accept).
- `TextDialog` stays in use for `:messages`, `:log`, and the dialog `?`.
- Issue #26 adds pure row builders and re-points both injected pickers (`PrActions`,
  `WorkItemActions`) at `MenuDialog.Run<T>` — no changes to the menu component itself.
- `Enter` on a filter that matches nothing dismisses the menu without choosing, rather than
  doing nothing — one Open path, and it matches `Enter`-closes everywhere else in the app.
- A long description (the command palette's) truncates in a narrow menu.
  `FormatRows(width)` truncates deliberately; the string cheatsheet never wrapped either.
