# 0014 — One count-aware scroll seam; dialogs share the vim router

Status: Accepted · Date: 2026-07-06

## Context

The vim layer (ADR 0007) initially routed only the top-level list views. Movement
was forwarded through `ListNavigation`, a `ListView`-only adapter that called the
widget's `MoveDown`/`MovePageDown`/… primitives. The detail dialogs (work item, PR,
diff review) hand-rolled their own `switch (token)` key handlers, so they shared
neither the router, the count logic, nor the scrolling behavior, and their bodies
(read-only `TextView`s) had no vim scrolling at all. Adding vim count prefixes
(`5j`, `10G`, `3]`) on top of that duplication would have meant four copies of the
same repeat-N loop.

Two Terminal.Gui 2.4.16 facts shaped the design, confirmed by headless probes:

- `View.InvokeCommand(Command.Down/Up/Start/End/PageDown/PageUp)` is a *uniform*
  scroll API: it moves `SelectedItem` on `ListView` and `CurrentRow` on a read-only
  `TextView`. One code path drives both widget kinds.
- `TextView.Viewport.Y` clamps at `Height-1` and is **not** a reliable scroll
  offset. Scroll assertions must read `CurrentRow` (or `SelectedItem`), never
  `Viewport.Y`.

## Decision

- **`KeymapRouter` owns counts.** A leading digit run (with no multi-key sequence
  pending) accumulates into `KeyResult.Count`; a bare `0` with no count started is
  ignored (there is no line-start motion), so it can only extend an existing count
  (`10j`). The router remains pure — no Terminal.Gui types — so counts are unit-tested
  in isolation and threaded through the shell and every dialog.
- **`VimScroll` replaces `ListNavigation`.** `VimScroll.Apply(View, AppCommand,
  int? count)` maps the movement commands onto any focusable scrollable view via
  `InvokeCommand`, applying the count (goto-line for `Ngg`/`NG`, repeat for
  `j`/`k`, half-page × N for `Ctrl-d`/`Ctrl-u`). `Ctrl-d`/`Ctrl-u` are now a true
  half page everywhere, including lists.
- **Dialogs adopt the router.** `PrDetailDialog`, `WorkItemDetailDialog`, and
  `DiffReviewDialog` each get a `KeymapRouter` and route keys through it: `Pending`
  swallows the key, `Matched` dispatches, and the dialog only marks a key handled
  when it actually acts — so unclaimed keys still reach native widget behavior
  (e.g. `Enter` opens the highlighted diff file). Movement dispatches to
  `VimScroll` on the body or focused pane; `?` opens a shared, scrollable
  `TextDialog` key reference.

## Consequences

Count logic and scrolling live in one tested place; the dialogs are thin. Scroll
tests assert on `SelectedItem`/`CurrentRow` (a comment records why `Viewport.Y` is
off-limits). Digits are reserved for counts, so sections moved off `1`/`2` onto the
`gt`/`gT`/`g1`/`g2` chords. Cost: the router now carries a small amount of numeric
state, and Terminal.Gui's per-key event delivery to the dialogs remains the only
seam not exercised in CI (mitigated by headless view-level tests that drive
`NewKeyDownEvent` through real routing).

## Amendment (2026-07-27) — `DialogKeyRouter`, one adapter instead of four copies

"Dialogs adopt the router" above gave each dialog its own `KeymapRouter` but left the
state machine around it hand-rolled, so the same ~33 lines existed four times
(`WorkItemDetailDialog`, `PrDetailDialog`, `ThreadViewDialog`, `DiffReviewDialog`),
differing only in the `KeyScope` literal. An Esc-or-pending fix had to land four times.

`Screens/DialogKeyRouter` now owns that machine — tokenize, snapshot `HasPending`, `Feed`,
`Pending` swallows, `Matched` marks the key handled only when the dialog acted, `Esc` clears
a pending sequence before it closes — taking `(KeyBindingTable, KeyScope,
Func<AppCommand, int?, bool> dispatch, Action requestClose)`. Each dialog keeps only its
`Dispatch` verb table. It lives in `Screens/` because it sets `Handled` on a Terminal.Gui
`Key`: that rules out `ViewModels/`, which ADR 0004's `ViewModelPurityTests` polices, and
equally `Input/`, which is deliberately Terminal.Gui-free (`KeymapRouter`: "Pure logic — no
Terminal.Gui types"). `Screens/VimScroll` is the precedent — a shared key-behaviour helper
that is not itself a screen.

`Screens/TextDialog` keeps its own copy and is **not** migrated. It looks like a fifth
instance of the machine but closes on the literal tokens `q`/`Esc`/`Enter` rather than on the
commands they resolve to, so routing it through the adapter would newly close the overlay on
`h` (Global → `Back`) and `l`/`o` (Global → `Open`). That is a behaviour change, not an
extraction.

Two constraints the shape has to respect:

- **One instance per dialog, subscribed wherever that dialog needs it.** Three dialogs
  subscribe both the body `TextView` and the dialog itself (a focused read-only `TextView`
  swallows runes), so the count/pending state must be shared across both delivery points —
  two instances would fork it and break `5j` across a focus change. `DiffReviewDialog`
  subscribes once.
- **`DiffReviewDialog`'s search-bar suppression stays caller-side**, as a guard in its own
  subscription lambda rather than a parameter on the adapter. A suppression predicate would
  be null for the other three, i.e. a behaviour switch on a shared type.

That guard is live, not defensive. A headless probe against Terminal.Gui 2.4.17 — a focused
child `TextField`, keys injected at the parent, which is the production entry point since the
driver raises `KeyDown` on the top-level `Dialog` — shows runes, `Ctrl+D` and `Home` are
consumed by the field, while `Enter`, `Esc`, `Tab`, `CursorDown`, `PageDown` and `Ctrl+U`
still reach the parent. Only the ones `KeyTokenizer` recognises can act, which excludes
`PageDown` — it tokenizes to `null` and was never a threat, despite being the key the review
that raised this originally named. The rest are recognised and bound (`Ctrl+U` → `HalfPageUp`,
`Tab` → `CyclePane`, `Esc` → close), so the guard is doing real work; do not narrow it to
control chords on the strength of the one case pinned by test.
`DiffReviewDialogKeyTests` pins `Ctrl+U`, which would half-page the pane behind the bar.

Cost: one delegate hop per key, and each dialog names its `KeyScope` twice — once to the
adapter, once for `HelpText.ForDialog` — while holding the `KeyBindingTable` it also hands
the adapter. Re-exposing scope and table off the adapter would delete that repetition, but
the table is the *dialog's* dependency, not the adapter's: `PrDetailDialog` and
`DiffReviewDialog` forward it to the child dialogs they open, so they would keep the field
regardless, and the adapter would become a lookup for a concern it does not own.
`KeymapRouter.Table`, which used to serve that read-back, is gone — the extraction left it
with no callers.
