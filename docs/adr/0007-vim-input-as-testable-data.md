# 0007 — Vim input as testable data, verified without a live terminal

Status: Accepted · Date: 2026-07-04

## Context

The vim key layer is the product's core feel, and it must be correct
(sequences like `gg`, prefix sharing `gg`/`gx`, scope fallback). Terminal.Gui
drives real keystrokes through an OS input thread that cannot run in CI, and
piped-stdin harnesses (`script`) do not deliver input to that thread.

## Decision

Model the key layer as pure data + pure functions, isolated from Terminal.Gui:

- `KeyBindingTable` — bindings as `(token-sequence, command)` per scope; one
  edit point for a future remapping config.
- `KeymapRouter` — a stateful sequencer over string tokens ("j", "C-d", "Esc"),
  returning `Matched`/`Pending`/`None`. No Terminal.Gui types.
- `KeyTokenizer` — the *only* place Terminal.Gui `Key` meets our tokens;
  unit-tested with real `Key` values (`new Key('j')`, `Key.Tab.WithShift`).
- `ShellViewModel` / `PaletteCommandParser` — dispatch and `:`-command parsing,
  pure.

Verification is layered: (1) unit tests for each pure piece; (2) an
integration test that runs the whole glue chain `Key → KeyTokenizer →
KeymapRouter → ShellViewModel` with real `Key` objects; (3) a manual
smoke via a real PTY confirming launch, render, `?` help overlay, and `:q`
exit. Terminal.Gui's own event delivery is the only unverified-in-CI seam, and
it is a thin adapter (`CobaltShell.WireKeys`).

## Consequences

The behavior that carries product risk is fully tested and fast. The Terminal.Gui
binding stays a thin, reviewable adapter. Cost: the adapter itself relies on
manual/PTY verification rather than CI.

## Amendment (2026-07-27) — `ShellCommandRouter`, the routing hop

The chain above ends at `ShellViewModel`, but a matched command is not dispatched there
directly: `CobaltShell.Dispatch` first has to decide *which screen* it targets, and that
decision depends on the active section and on workspace pane focus (ADR 0024). Left in the
shell, that routing was ~147 lines of section branching interleaved with Terminal.Gui side
effects. It was not untestable — `ShellKeepAliveTests` and `ShellWorkspaceLayoutTests` drive
keys into a headless shell — but every routing assertion had to construct the whole shell and
read the decision back out of a Terminal.Gui side effect, so the chain's last hop was the only
one this ADR's "pure data + pure functions" premise did not actually hold for.

The chain is therefore `Key → KeyTokenizer → KeymapRouter → ShellCommandRouter → ShellViewModel`.
`ViewModels/ShellCommandRouter` maps `(AppCommand, count, active section, workspace state,
"is the PR list built")` to a `ShellAction` the shell merely performs. It is a view-model, not
a pure function: `WorkspaceViewModel.CyclePane`/`FocusLeft`/`FocusRight` both move focus and
report whether they consumed the key, so the mutation *is* the decision. Living in `ViewModels/`
puts it under ADR 0004's `ViewModelPurityTests`, with that guard's stated limit: it is
signature-level, so it proves no Terminal.Gui type appears in the router's API. Keeping the one
view-backed fact the router needs — whether the PR list screen was built — behind a `Func<bool>`
is a design choice, not something the reflection backstop can enforce.

Deliberately narrow: the router owns only the arms that branch on section or workspace state
(`CyclePane`, `FocusLeft`/`FocusRight`, the PR sub-tab intercept, movement, `Refresh`,
`FilterStart`, `Open`) and returns `NotRouted` for everything else, so the shell's remaining
verbs keep their existing handling unchanged. Two consequences worth naming: a `NotRouted`
action carries the *rewritten* command (Tab → `NextTab` while the preview is hidden), and
`Consumed` is distinct from `NotRouted` because some arms — `/` in the PR section — must do
nothing *quietly*, where declining would surface a "not available here" message.
