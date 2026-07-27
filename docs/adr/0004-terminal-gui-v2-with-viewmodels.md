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
implemented interface. Note its limit: the check is **signature-level reflection only**, so it
proves no Terminal.Gui type appears in a view-model's API — never that a method *body* is free of
them. Body-level purity is a review obligation, not a tested one.

## Amendment (2026-07-27) — where screen state may *not* go: the two file identities

"All screen state lives in `ViewModels/`" has one carve-out, and it is not stylistic.

In the diff review, **the file under the tree cursor and the file on screen are different things**.
`PrDiffViewModel.SelectFileAsync` moves `SelectedFile` synchronously and publishes the fetched diff
only when it lands, so for the duration of that await the cursor has already moved to the next file
while the previous one is still displayed. Anything acting on the *diff* must key on
`CurrentDiffPath` / `CurrentDiffSnapshot`; only the file-tree cursor may use `SelectedFile`. Four
shipped bugs came from confusing them (line comments, thread replies, mark-viewed, `]t`/`[t`).

The consequence for placement: a view-model may own **one** of those identities, never both. The
extraction of `DiffReviewViewModel` (issue #77) therefore moved the DISPLAYED-side state — the
search machine, fold state, the rendered path, the row maps, the pane mode — plus the
identity-neutral file tree (keyed by node path), but deliberately left `_fileIndex` (CURSOR) in
`DiffReviewDialog`. It is written synchronously inside the async select that must stay in the view,
and hoisting it would have put both identities behind one object boundary — precisely the confusion
the four bugs grew from.

State on my mind, not logic: the boundary is **no cursor *state* in the view-model**, not "no cursor
logic". `StepFileTarget`, `NextUnviewedTarget` and `FileIndexForPath` all compute indexes *into*
`vm.Files` that the dialog then assigns to `_fileIndex` — the view-model is the authority on where
the cursor goes, it just never holds where the cursor *is*, and it takes the current path as a
**parameter** rather than reading it. So the view-model never references `SelectedFile` at all: that
is grep-checkable, and it is the first thing to attack when reviewing a change to this dialog.

The move was bounded, not exhaustive — `TitleFor` and the diff header's formatting are pure and
identity-neutral and would be legitimate view-model material, but they are ~40 low-risk lines tied
to chrome-only renders and were left for a follow-up rather than widening a high-risk diff.
`NavHunk`/`NavThread` are the more substantial remainder: both derive a target line from the
displayed snapshot with no widget contact, and are the natural next extraction.

Two smaller things also stay in the view for a mechanical reason worth recording, since both look
like pure logic that "should" have moved: the diff pane's kept cursor row and the file list's
fallback selection are read off the widget **around** a source assignment (`SetSource` nulls a
`ListView`'s selection), so their ordering relative to that assignment is load-bearing. The pure
decisions moved; the reads that must straddle the widget write did not.
