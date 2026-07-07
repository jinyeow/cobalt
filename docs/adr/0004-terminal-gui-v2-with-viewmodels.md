# 0004 — Terminal.Gui v2 behind a TUI-free view-model layer

Status: Accepted · Date: 2026-07-04

## Context

Cobalt needs multi-pane layout, focus management, lists, and text rendering.
Terminal.Gui v2 (stable 2.4.x) is the most mature C# TUI framework. But its
API has churned across the v2 series, and framework-coupled logic is hard to
test in CI (no real terminal).

## Decision

- Take the Terminal.Gui v2 dependency, pinned to a minor version.
- All screen state, commands, and formatting live in `ViewModels/` classes that
  reference **only** `Cobalt.Core` — never Terminal.Gui types. Views in
  `Screens/` are thin bindings from view-model state to widgets.
- The vim key layer is a pure `KeymapRouter` (key sequence → command name)
  unit-tested without a terminal; views feed it key events.

## Consequences

Every behavior that matters is testable in CI. Terminal.Gui upgrades touch only
`Cobalt.Tui`. Cost: some binding boilerplate per screen.
