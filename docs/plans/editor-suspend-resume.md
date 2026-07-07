# Plan: $EDITOR suspend/resume round-trip (closes TODO(M6))

Goal: launching a full-screen `$EDITOR` from a dialog gives the editor a clean
terminal (stdin/stdout owned by the child, Terminal.Gui quiesced) and cleanly
restores the TUI on exit. Implements the `TODO(M6)` in
`src/Cobalt.Tui/App/CobaltShell.cs`.

## Verified primitives (do not re-derive)

Under a real PTY, on the **UI thread**: `app.Driver.Suspend()` → start child →
`WaitForExit` → `app.LayoutAndDraw(true)` works. The child reads uncorrupted
input from the tty, Terminal.Gui's input thread does not steal keystrokes, and
the screen repaints correctly afterwards. There is no public `Resume()`;
`IDriver.Init()` throws. So the primitive pair is `Suspend()` +
`LayoutAndDraw(true)`, both driven from the UI thread with the main loop
**blocked** for the child's whole lifetime.

## Design

### Threading: block the UI thread inside `app.Invoke`

Dialogs launch the editor from fire-and-forget background tasks
(`_ = CommentAsync()` in WorkItemDetailDialog/PrDetailDialog/DiffReviewDialog).
Keep that structure. The launcher hands the *terminal-owning* section to a
suspender that marshals it onto the UI thread:

- Background task calls `suspender.RunSuspendedAsync(body, ct)` and awaits a
  `TaskCompletionSource<int>`.
- The suspender queues one callback via `app.Invoke`. That callback — running
  on the UI thread — does `suspend(); try { exit = body(); } finally { resume(); }`
  where `body` synchronously starts the process and `WaitForExit()`s, then
  completes the TCS (result or exception).

Rationale: this reproduces the verified probe *exactly*. Parking the main loop
inside the invoked callback is the guarantee that Terminal.Gui cannot draw,
relayout, or dispatch other `Invoke`s while the child owns the terminal. The
alternative (async suspend from the background thread, main loop still
pumping) leaves the loop free to draw over the editor and was not what the
probe validated. `app.Invoke` from inside a modal dialog's nested run loop is
already proven in this codebase (`WorkItemDetailDialog.OnChanged`).

Consequences to document in code comments:
- `RunSuspendedAsync` must never be awaited synchronously (`.Wait()`) on the
  UI thread — deadlock. All current call paths are background tasks; fine.
- While the editor runs, cancellation is not observed (the UI thread is
  blocked, so the dialog cannot close and cancel its CTS anyway). `ct` is
  checked once before suspending.

### The testable seam

Per ADR 0004/0007 all logic must be unit-testable without a terminal, so the
orchestration is a pure class parameterized on three delegates; only a
3-lambda factory touches Terminal.Gui:

```csharp
// src/Cobalt.Tui/Editor/ITerminalSuspender.cs
public interface ITerminalSuspender
{
    /// <summary>Runs body with the TUI suspended; suspend → body → resume,
    /// resume always fires (even when suspend or body throws).</summary>
    Task<int> RunSuspendedAsync(Func<int> body, CancellationToken cancellationToken = default);
}

/// <summary>No terminal to suspend — runs body inline (tests, headless).</summary>
public sealed class InlineTerminalSuspender : ITerminalSuspender { ... }

// src/Cobalt.Tui/Editor/UiThreadSuspender.cs — pure, fully unit-tested
public sealed class UiThreadSuspender(
    Action<Action> invokeOnUiThread, Action suspend, Action resume) : ITerminalSuspender
{ ... TCS pattern described above ... }

// src/Cobalt.Tui/App/TerminalGuiSuspender.cs — thin adapter, PTY-verified only
public static class TerminalGuiSuspender
{
    public static ITerminalSuspender For(IApplication app) => new UiThreadSuspender(
        app.Invoke,
        () => app.Driver?.Suspend(),
        () => app.LayoutAndDraw(true));
}
```

Ordering/exception contract (unit-tested against `UiThreadSuspender` with
recording delegates): resume runs in a `finally` that also covers `suspend()`,
so a throw during suspend still attempts `LayoutAndDraw(true)` (best effort to
repair a half-suspended screen); the original exception propagates to the
returned task.

### Launcher integration

`ProcessEditorLauncher` currently has unused `beforeLaunch`/`afterExit` Action
hooks. Replace them with an `ITerminalSuspender` constructor parameter
(default `new InlineTerminalSuspender()` so existing tests and construction
sites stay valid). `LaunchAsync` becomes: resolve/tokenize command, build
`ProcessStartInfo` (unchanged), then

```csharp
return suspender.RunSuspendedAsync(() =>
{
    using var process = Process.Start(info)
        ?? throw new EditorLaunchException($"could not start editor '{parts[0]}'");
    process.WaitForExit();          // synchronous by design: UI thread is parked
    return process.ExitCode;
}, cancellationToken);
```

`Process.Start` failures (`Win32Exception` — editor missing) are wrapped in a
new `EditorLaunchException` carrying a user-facing message
(`"could not start editor 'nvim' — check $VISUAL/$EDITOR"`). The old
`InvalidOperationException` for a null process becomes `EditorLaunchException`
too.

### Failure surfacing in dialogs

Today an exception from `EditAsync` inside `_ = CommentAsync()` vanishes into
an unobserved task (user presses `c`, nothing happens, no message). Each of
the six `EditAsync` call sites (WorkItemDetailDialog ×3, PrDetailDialog ×2,
DiffReviewDialog ×1) wraps the await in
`try { ... } catch (EditorLaunchException ex) { _app.Invoke(() => _log($"editor failed: {ex.Message}")); }`
(DiffReviewDialog/PrDetailDialog use their existing log delegates). This is
thin view glue; correctness of the message path is covered by the PTY smoke,
not unit tests (consistent with ADR 0004).

### Wiring

`CobaltShell` ctor replaces the TODO(M6) block with:

```csharp
_editor = editor ?? new EditorService(new ProcessEditorLauncher(
    Environment.GetEnvironmentVariable, TerminalGuiSuspender.For(app)));
```

`EditorService` is untouched. `CobaltTuiApp` passes `editor: null`, so the
shell default is the production path.

## What is unit-testable vs PTY-only

Unit-testable (write these tests FIRST):
- `UiThreadSuspender` ordering contract: suspend before body, resume after,
  via the injected invoke delegate; resume on body throw; resume on suspend
  throw; exit-code passthrough; exception propagation; single execution; early
  cancellation skips suspend entirely.
- `ProcessEditorLauncher` behavior through a fake/real suspender: process runs
  inside the suspender body (not outside it), exit code round-trips, missing
  editor → `EditorLaunchException`, and — composing real `UiThreadSuspender`
  with recording delegates + a bogus `$EDITOR` — resume still fires when
  `Process.Start` throws.
- Everything existing (tokenizer, `EditorService`) stays green.

PTY-only (cannot be unit-tested, verified by smoke):
- That `app.Invoke` runs the callback on the real UI thread and parks the loop.
- That `Driver.Suspend()` actually releases the tty and `LayoutAndDraw(true)`
  repaints (the three lambdas in `TerminalGuiSuspender.For`).
- The dialog-level catch → message-bar path.

## TDD task list

Each task: write the failing test(s), run `dotnet test` to see red, implement,
green. Tasks are ordered so the tree compiles and all tests pass after each.

1. **`ITerminalSuspender` + `InlineTerminalSuspender`**
   - Test first (`tests/Cobalt.Tui.Tests/Editor/InlineTerminalSuspenderTests.cs`):
     `RunSuspendedAsync` returns the body's value; body exception propagates as
     a faulted task; pre-canceled token → `OperationCanceledException` and body
     never runs.
   - Implement `src/Cobalt.Tui/Editor/ITerminalSuspender.cs` (interface +
     `InlineTerminalSuspender` in the same file; inline impl is
     `ct.ThrowIfCancellationRequested(); return Task.FromResult(body());` with
     exceptions surfaced via the task, e.g. wrap in try/catch →
     `Task.FromException<int>`).

2. **`UiThreadSuspender` ordering contract**
   - Tests first (`tests/Cobalt.Tui.Tests/Editor/UiThreadSuspenderTests.cs`),
     using an event log `List<string>` and `invoke: a => a()` (synchronous
     inline dispatch stands in for the UI thread):
     - `Runs_Suspend_Body_Resume_In_Order` — log is exactly
       `["suspend", "body", "resume"]` and result is the body's exit code.
     - `Body_Runs_Via_The_Invoke_Delegate` — with
       `invoke: a => { log.Add("invoke"); a(); }`, "invoke" precedes "suspend"
       and invoke is called exactly once (no double-suspend).
     - `Resume_Fires_When_Body_Throws` — body throws `InvalidOperationException`;
       log ends with "resume"; awaiting the task rethrows that exception.
     - `Resume_Fires_When_Suspend_Throws` — suspend throws; body never runs;
       log contains "resume"; task faults with the suspend exception.
     - `Resume_Exception_Faults_The_Task` — body succeeds, resume throws; task
       faults (never silently loses a resume failure).
     - `Precanceled_Token_Skips_Suspend` — `await` throws
       `OperationCanceledException`, log empty.
     - `Completes_From_A_Queued_Invoke` — invoke that stores the action and
       runs it later on another thread; assert the awaiting task only completes
       after the deferred run (proves TCS wiring, no inline shortcut). Use
       `TaskCreationOptions.RunContinuationsAsynchronously` on the TCS.
   - Implement `src/Cobalt.Tui/Editor/UiThreadSuspender.cs` per the design
     sketch (single try/finally: `suspend()` inside the try, `resume()` in the
     finally; all exceptions → `tcs.TrySetException`; cancellation checked
     before calling `invoke`).

3. **`EditorLaunchException` + `ProcessEditorLauncher` runs inside the suspender**
   - Tests first (extend
     `tests/Cobalt.Tui.Tests/Editor/ProcessEditorLauncherTests.cs`; these
     start real trivial processes — `sh`/`true` — matching the repo's
     Linux-only CI; keep tokenizer tests untouched):
     - `Process_Runs_Inside_Suspender_Body` — fake `ITerminalSuspender` that
       logs "enter"/"exit" around invoking the body; `env: EDITOR = "sh -c 'exit 3'"`
       (tokenizer yields `sh -c "exit 3"`, the file path lands in `$0`);
       assert `LaunchAsync` returns 3 and log is `["enter", "exit"]`.
     - `Editor_Writes_File_Through_Suspender` — `EDITOR = "sh -c 'echo edited > \"$0\"'"`;
       assert exit 0 and the temp file contains "edited" (proves the child ran
       to completion inside the body, not fire-and-forget).
     - `Missing_Editor_Throws_EditorLaunchException` —
       `EDITOR = "cobalt-no-such-editor-xyz"`, fake suspender that just runs
       the body; `await Assert.ThrowsAsync<EditorLaunchException>(...)`.
     - `Resume_Still_Fires_When_Editor_Is_Missing` — compose the **real**
       `UiThreadSuspender(a => a(), log suspend, log resume)` with the bogus
       editor; assert the task faults with `EditorLaunchException` AND the log
       ends with "resume". This is the end-to-end ordering guarantee for the
       "process throws" edge case.
     - `Default_Suspender_Is_Inline` — constructing
       `new ProcessEditorLauncher(env)` (no suspender) still launches
       (`EDITOR = "true"` → exit 0), proving back-compat for existing callers.
   - Implement: add `src/Cobalt.Tui/Editor/EditorLaunchException.cs`
     (`sealed class EditorLaunchException(string message, Exception? inner = null)
     : Exception(message, inner)`); rewrite `ProcessEditorLauncher` — delete
     the `beforeLaunch`/`afterExit` parameters (they have no callers passing
     non-null), add `ITerminalSuspender? suspender = null` defaulting to
     `InlineTerminalSuspender`, move `Process.Start` + synchronous
     `WaitForExit()` into the suspender body, catch
     `System.ComponentModel.Win32Exception` → wrap in `EditorLaunchException`.
     Update the class doc comment to describe the suspender contract.

4. **Terminal.Gui adapter + shell wiring** (no new unit tests possible — this
   is the PTY-only layer; the task is done when the solution builds and all
   prior tests stay green)
   - Add `src/Cobalt.Tui/App/TerminalGuiSuspender.cs`: the static `For(IApplication)`
     factory shown in the design — exactly three lambdas
     (`app.Invoke`, `app.Driver?.Suspend()`, `app.LayoutAndDraw(true)`), no
     logic. Doc-comment that it is verified by the PTY smoke, not unit tests,
     and why (`IDriver.Init` throws; no public Resume).
   - Modify `src/Cobalt.Tui/App/CobaltShell.cs`: delete the TODO(M6) comment,
     wire `new ProcessEditorLauncher(Environment.GetEnvironmentVariable,
     TerminalGuiSuspender.For(app))`.

5. **Dialog failure surfacing**
   - No unit tests (dialogs are view glue per ADR 0004; verified in the PTY
     smoke by pointing `$EDITOR` at a nonexistent binary).
   - Modify `src/Cobalt.Tui/Screens/WorkItemDetailDialog.cs` (`CommentAsync`,
     `EditDescriptionAsync`, `PromptAndRun`),
     `src/Cobalt.Tui/Screens/PrDetailDialog.cs` (both `EditAsync` sites),
     `src/Cobalt.Tui/Screens/DiffReviewDialog.cs` (one site): wrap the
     `await editor.EditAsync(...)` in
     `try { ... } catch (EditorLaunchException ex) { _app.Invoke(() => _log($"editor failed: {ex.Message}")); return; }`
     (adapt to each dialog's existing log/message delegate names).

6. **PTY smoke + docs**
   - Run the smoke procedure below; fix anything it exposes.
   - Write ADR 0009 (see last section). Update `docs/PLAN.md`'s M6 entry if it
     tracks this item.

## Files to add / modify

Add:
- `src/Cobalt.Tui/Editor/ITerminalSuspender.cs` (interface + `InlineTerminalSuspender`)
- `src/Cobalt.Tui/Editor/UiThreadSuspender.cs`
- `src/Cobalt.Tui/Editor/EditorLaunchException.cs`
- `src/Cobalt.Tui/App/TerminalGuiSuspender.cs`
- `tests/Cobalt.Tui.Tests/Editor/InlineTerminalSuspenderTests.cs`
- `tests/Cobalt.Tui.Tests/Editor/UiThreadSuspenderTests.cs`
- `docs/adr/0009-editor-suspend-resume.md`

Modify:
- `src/Cobalt.Tui/Editor/ProcessEditorLauncher.cs`
- `src/Cobalt.Tui/App/CobaltShell.cs`
- `src/Cobalt.Tui/Screens/WorkItemDetailDialog.cs`
- `src/Cobalt.Tui/Screens/PrDetailDialog.cs`
- `src/Cobalt.Tui/Screens/DiffReviewDialog.cs`
- `tests/Cobalt.Tui.Tests/Editor/ProcessEditorLauncherTests.cs`

Untouched: `EditorService.cs`, `IEditorLauncher.cs`, `EditorServiceTests.cs`,
`CobaltTuiApp.cs`.

## Verification

### Unit tests (all must pass; the new ones are listed in tasks 1–3)

`dotnet test` — full suite. New coverage summary: suspend→body→resume ordering;
resume on body/suspend/Process.Start failure; resume-exception propagation; no
double-suspend; exit-code round-trip; pre-cancel skips suspend; launcher runs
the child inside the suspender body; missing editor → `EditorLaunchException`;
parameterless construction unchanged.

### PTY smoke (the only verification for `TerminalGuiSuspender` + dialog glue)

Reuse the harness pattern from the session scratchpad
(`scratchpad/pty_run.py` — `pty.fork()`, `TERM=xterm-256color`, write
keystrokes to the fd, read/strip ANSI, plus `scratchpad/tguiprobe/` for the
probe-app shape). Because the real dialogs need an ADO connection, smoke at
two levels:

1. **Adapter probe (required gate).** Update the scratchpad tguiprobe-style
   app so its `e` handler goes through the *production* stack —
   `new EditorService(new ProcessEditorLauncher(env, TerminalGuiSuspender.For(app)))`
   called from a fire-and-forget background task (`_ = Task.Run(...)`),
   mirroring the dialogs — with a stand-in editor script:
   ```sh
   #!/bin/sh
   # fake-editor: proves tty ownership and performs an edit
   printf 'EDITOR-OWNS-TTY\n'
   read line < /dev/tty
   echo "typed:$line" > /tmp/smoke-child.txt
   echo "edited body" > "$1"
   ```
   Drive via the PTY script: launch, press `e`, wait, type `hello q\r`
   (the stray `q` is the regression check), read output, wait for redraw,
   press `q` to quit. Assert:
   - PTY output contains `EDITOR-OWNS-TTY` (child owned stdout).
   - `/tmp/smoke-child.txt` contains `typed:hello q` (child owned stdin; the
     stray `q` did NOT quit the app — the app must still be alive afterwards).
   - Post-exit PTY output contains the probe window chrome again (TUI redrew)
     and the probe label shows the edited text round-tripped
     (`EditAsync` returned "edited body").
   - Re-run with `EDITOR=cobalt-no-such-editor-xyz`: app stays alive and shows
     the failure message (exercises `EditorLaunchException` handling).
   - Optionally re-run with `EDITOR=nano` and eyeball interactively.
2. **Full-app spot check (best effort).** With a real ADO context configured:
   `EDITOR` set to the script above, open a work item, press `c`, confirm the
   comment text lands and the screen restores. Manual; not CI.

## ADR to write

`docs/adr/0009-editor-suspend-resume.md` — "Suspend Terminal.Gui by parking
the UI thread around external processes". Records: (a) Terminal.Gui v2 has
`Suspend()` but no public resume — `LayoutAndDraw(true)` is the empirically
verified restore; (b) the external process runs *synchronously on the UI
thread* inside `app.Invoke`, deliberately blocking the main loop so nothing
draws while the child owns the terminal; (c) the `ITerminalSuspender` /
`UiThreadSuspender` seam keeps the ordering contract unit-testable per ADR
0004, with only a 3-lambda adapter left to PTY verification; (d) consequence:
cancellation is not observed while the editor is open, and `RunSuspendedAsync`
must never be synchronously awaited on the UI thread.
