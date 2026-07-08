# 0020 — In-TUI text input for short entry; `$EDITOR` for long-form

Status: Accepted · Date: 2026-07-08

> Note: ADR 0019 (hybrid theming) is developed on a sibling branch; this feature takes the
> next free number, 0020.

## Context

Comments, replies, and small prompts (a thread id, an assignee) opened `$EDITOR` via the
suspend/resume handoff (ADR 0009): park the UI thread, hand the tty to the child, restore on
exit. That handoff is the root of a whole bug class on Windows Terminal — neovim starting
slowly (its terminal-capability/background DSR query times out during the handoff, E1568),
Win32-console driver quirks (ADR 0016), and a brief blank screen — and it can only be UAT'd,
never unit-tested. For a one-line comment (worst case: the thread-id prompt launched a full
editor to type *a number*) that cost is absurd.

## Decision

- **Hybrid, à la lazygit.** Handle short/frequent text entry with an **in-TUI editable field**;
  keep `$EDITOR` for genuinely long-form text and as an escape hatch. Built-in widgets for the
  common case, the real editor only when it earns its keep.

- **A UI-free seam, `ITextInput.ReadAsync(TextInputRequest) → string?`.** ViewModels stay
  Terminal.Gui-free (ADR 0004); the interface carries no TG types, so both implementations and
  test fakes are trivial. Two implementations: `TuiTextInput` (a `Dialog` + editable `TextView`,
  or a single-line `TextField`) and `EditorTextInput` (the `$EDITOR` fallback / hatch target).
  Unlike the handoff, the in-TUI path is **headless-testable** by driving `NewKeyDownEvent`.

- **In-TUI:** line comments, thread replies, PR replies, PR-level comments (multi-line); the
  thread-id prompt and assignee (single-line). **Stays `$EDITOR`:** work-item descriptions
  (prefilled, long-form markdown). Tags stays `$EDITOR` for now (its own UX PR).

- **Keys.** **Enter submits.** In a multi-line field a chord inserts a newline —
  **Ctrl-Enter / Shift-Enter where the terminal delivers them distinctly, else Ctrl-J** (literal
  line-feed, always delivered); the widget probes and uses what works. **Esc** cancels
  (→ `null`). **Ctrl-E** hands the current buffer to `$EDITOR` and returns to the field for
  review (not auto-submit). Single-line fields submit on Enter with no newline. The Dialog's
  default-accept is suppressed (`Accepting → e.Handled = true`) so Enter is ours.

- **ADR 0009 stays Accepted, scope narrowed** to descriptions and the Ctrl-E hatch. No global
  config toggle to force `$EDITOR` everywhere (the hatch covers it — YAGNI). Vim-modal editing
  inside the field is deferred to its own ADR/PR.

## Consequences

- The common comment/reply path no longer suspends Terminal.Gui, so the E1568 slow-start,
  driver quirks, and blank screen simply do not occur there; entry is instant and unit-tested.
- `$EDITOR` no longer needs to be set/installed to leave a comment; it remains for long-form and
  on demand (Ctrl-E).
- The exact newline chord is environment-dependent; the widget documents what it resolved, and
  the hint line shows the active keys.
