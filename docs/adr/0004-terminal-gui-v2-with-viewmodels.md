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

## Amendment (2026-07-22) — the `IUiPost` marshalling seam

Background/thread-pool continuations must hop to the UI thread before touching widgets.
Rather than pass Terminal.Gui's `IApplication` around solely for its `Invoke`, cobalt threads a
one-member seam — `App/IUiPost.cs` (`void Post(Action)`) with an `ApplicationUiPost` adapter over
`IApplication.Invoke`. View-models and the post-continuation call sites depend on `IUiPost`, never
`IApplication`, and a test supplies an inline/recording fake.

Load-bearing contract: **`Post` always queues onto the main loop and never runs the action inline**,
even when the caller is already on the UI thread. FIFO queue ordering is what the coalescing gates
depend on — the diff-review stats-refresh gate and the PR-list count-badge coalescing both assume a
posted refresh runs *after* the events that queued it, so an implementation that ran `Post`
synchronously would break them.

The no-Terminal-Gui-in-`ViewModels` rule this ADR sets is enforced mechanically by
`ViewModelPurityTests`: a reflection backstop asserting that no type in `Cobalt.Tui.ViewModels`
names a Terminal.Gui type in any ctor parameter, field, property, method signature, base type, or
implemented interface.
