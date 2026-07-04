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
