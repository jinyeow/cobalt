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
  runners) whose unexpected exceptions no longer disappear into a broad catch.

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
