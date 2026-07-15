# 0013 — Narrow-at-operation exception handling with a global crash boundary

Status: Accepted · Date: 2026-07-06

## Context

The view-models originally wrapped each async load/mutation in a broad
`catch (Exception)` that funnelled everything into the `Error` message-bar state.
CodeQL's `cs/catch-of-all-exceptions` flagged eleven such sites. A blanket catch is
convenient but hides bugs: a `NullReferenceException` or `ArgumentException` from our
own code was silently rendered as an "error" string and swallowed, indistinguishable
from an expected network failure, with no stack trace anywhere.

We want two things at once: expected Azure DevOps failures should remain friendly,
in-app messages (SPEC §6, never a stack trace in the user's face); genuine bugs
should surface loudly and be recorded — without ever leaving the terminal in the
alternate-screen/raw state that Terminal.Gui puts it in.

## Decision

- **Narrow at the operation.** Every view-model catch is now filtered to the
  expected set via `AdoExceptions.IsExpected` (Cobalt.Core.Ado): `AdoApiException`,
  `HttpRequestException`, `System.Text.Json.JsonException`,
  `Azure.Identity.AuthenticationFailedException`, and `System.IO.IOException` — the
  same whitelist already used by `CobaltTuiApp` startup and `AuthCommands`.
  `OperationCanceledException` continues to be caught first and rethrown (a closing
  dialog / switched section is not an error). Anything outside the set propagates.

- **One global last-resort boundary.** `CobaltTuiApp.Run` wraps the run in a single
  `try/catch`. Because the run owns Terminal.Gui through `using var app = …Init()`,
  an exception unwinding out of `app.Run` disposes the application first — restoring
  the terminal — *before* the boundary writes to stderr, so the crash message lands
  on a clean screen rather than the TUI's alternate buffer.

- **Record, then report, then exit non-zero.** The boundary writes the full
  exception (type, message, stack, inner exceptions) to a crash-log file, prints
  `cobalt crashed — see <path>` to stderr, and returns a non-zero exit code.

- **Log location is XDG state.** `ConfigPaths.CrashLogFile()` resolves to the XDG
  *state* directory (`$XDG_STATE_HOME` or `~/.local/state`, `%LOCALAPPDATA%` on
  Windows) under `cobalt/crash.log` — kept out of the config directory so a crash log
  never sits beside user-edited configuration. Directories are created on demand.

- **Background faults route to the same log.** `TaskScheduler.UnobservedTaskException`
  and `AppDomain.CurrentDomain.UnhandledException` are wired at startup to append to
  the same file, covering the fire-and-forget tasks (identity resolution, list-action
  runners) whose unexpected exceptions no longer disappear into a broad catch. The
  unobserved-task handler calls `e.SetObserved()` *before* attempting the log write, so
  a throw from logging on the finalizer thread can never terminate the process; the
  unhandled-exception handler also prints `cobalt crashed — see <path>` to stderr.

- **Discarded dialog actions are observed.** A dialog verb (`v`/`c`/`x`/`C`/`A` on a PR,
  `s`/`c`/`e`/`a`/`t` on a work item, `c` in the diff view) is a fire-and-forget task.
  Each now runs through `FireAndForget.Observe`: a non-cancellation fault is recorded to
  the crash log **and** posted to the message bar immediately (marshalled to the UI
  thread), so a genuine bug surfaces loudly rather than lingering as an eventual
  `UnobservedTaskException`; an `OperationCanceledException` (dialog closed mid-op) stays
  silent, matching `IgnoreCancellationAsync`.

- **Crash-log writes are fully defensive.** `HandleCrash`/`LogBackgroundFault` catch
  *any* exception from the log write (not just `IOException`) — permission denied
  (`UnauthorizedAccessException`), a malformed `$XDG_STATE_HOME`
  (`ArgumentException`/`NotSupportedException`), or a full disk — and never rethrow;
  `HandleCrash` falls back to dumping the stack to stderr so the crash is never lost.

- **Timeouts are not silent cancellations.** An `HttpClient` timeout surfaces as an
  `OperationCanceledException` whose token is the client's internal timeout token, not
  the caller's. `AdoExceptions.IsTimeout` distinguishes the two: a cancel carrying the
  caller's own token is a genuine user/dialog cancel (rethrown, silent); any other is a
  timeout, surfaced in the message bar as an expected error instead of a blank pane.

- **A transient `Error` cannot carry a lasting degradation.** `Error` is cleared by the
  next operation, so it only describes a failure the user is about to see. When a failed
  fetch leaves a *capability* missing for longer than that, it needs its own state. The
  diff review's review-thread fetch is the case in hand. It runs on load and again after
  a comment or thread mutation — but a read-then-approve review triggers neither, so a
  failed load leaves no comment markers for the whole session. Worse, the next file
  selection publishes a diff, which overwrites the error header: a clean-looking diff and
  a `0 unresolved` title on a pull request that *does* carry review comments, with nothing
  on screen to say otherwise. A reviewer could approve blind.
  `PrDiffViewModel.ThreadsUnavailable` records the degradation and the title reads
  `comments unavailable` instead of a count, because **"no comments" and "comments
  unknown" are different facts and a count cannot express the second**.
  The state tracks the **most recent** thread fetch, not the first: every thread fetch
  sets it on failure and clears it on success, so a later refresh recovers the markers and
  the indicator, while a failed refresh after a successful load still degrades. A failure
  of the *mutation* itself (a rejected vote, a failed comment post) is not a threads
  problem and does not set it. The general rule: if a failure removes a capability for
  longer than the message bar's lifetime, model the capability's absence and keep it
  honest in both directions — do not rely on `Error` to keep saying it, and do not leave a
  recovered capability marked absent.

- **Formatting is a pure, injectable unit.** `CrashLog.Write(path, exception,
  timestamp)`/`CrashLog.Format(exception, timestamp)` take the timestamp as a
  parameter (never `DateTime.Now` internally), and the boundary itself
  (`HandleCrash`) takes the log path, timestamp, and stderr `TextWriter` as
  arguments, so both are unit-tested without a real crash or a real terminal.

## Consequences

- Expected ADO failures behave exactly as before (friendly message bar, rows
  cleared); the behavior change is that an unexpected type now escapes the view-model
  instead of being masked — caught by the boundary, logged with a stack, surfaced.
- The terminal is always restored on a crash, so a narrowed catch can no longer leave
  a corrupted session.
- One production broad catch is deliberately retained: `UiThreadSuspender`'s callback
  runs on the parked UI thread and *must* capture any throw from suspend/body/resume
  to marshal it into the returned task (otherwise it escapes the message loop and the
  awaiting editor call hangs). It carries a justification comment; CodeQL can dismiss
  that one site.
- The policy composes with ADR 0004 (UI-free view-models) and ADR 0009 (editor
  suspend/resume): narrowing lives in the view-models, restore lives at the one place
  that owns the application.
