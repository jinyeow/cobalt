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
