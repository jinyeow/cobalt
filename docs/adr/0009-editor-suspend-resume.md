# 0009 — Suspend Terminal.Gui by parking the UI thread around external processes

Status: Accepted · Date: 2026-07-04

## Context

Commenting, replying, and editing descriptions launch the user's `$VISUAL`/`$EDITOR`
(SPEC: git-commit-style round-trip). A full-screen editor (`nvim`, `nano`) needs
sole ownership of the terminal: stdin/stdout, cooked mode, and the alternate
screen buffer. While it runs, Terminal.Gui must not draw, relayout, or read
input, or the editor and the driver corrupt each other's screen and keystrokes.
This closes the `TODO(M6)` in `CobaltShell`.

Terminal.Gui v2 (2.4.16) exposes `IDriver.Suspend()` (releases the tty) but **no
public resume**: `IDriver.Init()` throws `NotSupportedException` on a second call.
Empirically, the working restore pair is `Suspend()` followed by
`app.LayoutAndDraw(true)`, both driven from the UI thread with the main loop
blocked for the child's whole lifetime.

## Decision

- **Park the UI thread.** Dialogs already launch the editor from fire-and-forget
  background tasks (`_ = CommentAsync()`). The launcher hands the terminal-owning
  section to an `ITerminalSuspender`, which marshals it onto the UI thread via
  `app.Invoke` and, on that thread, runs `suspend(); try { body(); } finally
  { resume(); }` where `body` starts the process and `WaitForExit()`s
  **synchronously**. Blocking the loop inside the invoked callback is the
  guarantee that nothing draws while the child owns the terminal — it reproduces
  the PTY-verified primitive exactly. The async alternative (suspend from a
  background thread, loop still pumping) leaves the loop free to draw over the
  editor and was not validated.

- **Keep the ordering contract unit-testable (per ADR 0004/0007).** The
  orchestration is a pure `UiThreadSuspender(invokeOnUiThread, suspend, resume)`
  parameterized on three delegates and fully unit-tested (suspend→body→resume
  ordering; resume fires on body throw, on suspend throw, and its own exception
  faults the task; exit-code passthrough; single execution; pre-cancel skips
  suspend; completion only after a deferred queued invoke runs). Resume lives in
  a `finally` that also covers `suspend()`, and the result is published only
  after `resume()` returns, so a half-suspended screen is still repaired and a
  resume failure is never silently lost. Only a 3-lambda adapter
  (`TerminalGuiSuspender.For`: `app.Invoke`, `app.Driver?.Suspend()`,
  `app.LayoutAndDraw(true)`) touches Terminal.Gui, verified by a PTY smoke.

- **Surface launch failures.** `ProcessEditorLauncher` runs the process inside the
  suspender body and wraps a failed `Process.Start` (`Win32Exception`, missing
  binary) in a new `EditorLaunchException` carrying a user-facing message. Each
  `EditAsync` call site in the dialogs catches it and writes
  `editor failed: …` to the message bar; previously the exception vanished into
  an unobserved task.

## Consequences

- `RunSuspendedAsync` must **never** be synchronously awaited (`.Wait()`) on the
  UI thread — that deadlocks. All current call paths are background tasks.
- Cancellation is **not observed** while the editor is open: the UI thread is
  parked, so the dialog cannot close and cancel its token anyway. The token is
  checked once before suspending.
- The 3-lambda adapter is the only part not covered by unit tests. It is verified
  by a `pty.fork()` smoke through the production stack
  (`EditorService`→`ProcessEditorLauncher`→`TerminalGuiSuspender`): the child
  prints to and reads a line from `/dev/tty` (proving tty ownership), a stray `q`
  typed to the editor does not quit the app (the input thread is quiesced), the
  TUI redraws with the round-tripped edited text after exit, and a missing editor
  keeps the app alive with the failure message. Note: the harness must answer the
  terminal's size (`ESC[18t`) and capability queries or Terminal.Gui never learns
  its geometry and draws empty frames — a property of the emulator, not the code.
