# 0016 — Terminal.Gui driver selection under multiplexers (`COBALT_DRIVER`)

Status: Accepted · Date: 2026-07-07

## Context

Local UAT on real hardware (the first session with a real TTY and a real org) found two
Priority-1 defects that only reproduce when cobalt runs **inside a terminal multiplexer**
— specifically `zellij` under Windows Terminal:

1. **Dropped keystrokes.** Rapid repeated motions lose input: pressing `j` six times from
   the top of a list lands on row 4, not row 6 (confirmed by *opening* the row — the real
   `SelectedIndex` was 4, so the presses were genuinely lost, not merely unpainted).
   Alternating keys (`kj`) and distinct commands register fine; only fast identical
   repeats drop.
2. **Broken editor suspend/resume.** `c` → `$EDITOR` launches nvim, but nvim renders a
   blank buffer that accepts no input and cannot be quit — the child never receives the
   console's stdin.

Both vanish immediately when cobalt is forced onto Terminal.Gui's `dotnet` driver, and
neither reproduces on a **bare** Windows Terminal (no multiplexer) on the default driver.

Root cause: Terminal.Gui's default `windows` driver talks to the Win32 **console** API
(`ReadConsoleInput`, console screen buffers). A multiplexer hands cobalt a
**pseudo-terminal**, not a real Win32 console, so the console-input path drops events and
`IDriver.Suspend()` cannot cede the console to the child editor. TG's `dotnet` and `ansi`
drivers are stdio/ANSI-based and work correctly through a pty. `DriverRegistry.GetDefaultDriver()`
picks `windows` on Windows and has no multiplexer awareness. This is **not** a cobalt logic
bug — `KeymapRouter`, `VimScroll`, and the suspend/resume orchestration are all correct and
unit-tested; the defect is entirely in driver selection.

The earlier `LayoutAndDraw(false)→(true)` change (`fb5b777`) was made on a repaint theory.
It is harmless and kept, but it did **not** and could not fix the double-input — the input
was dropped before any handler ran. The record is corrected here and in the code comment.

## Decision

- **Add a `COBALT_DRIVER` environment escape hatch.** `CobaltTuiApp.ResolveDriver` reads
  `COBALT_DRIVER`, matches it case-insensitively against `DriverRegistry.GetDriverNames()`
  (`windows`, `dotnet`, `ansi`), and passes the canonical name to `Application.Init(driverName)`.
  Unset → `null` → TG's auto-detected default (unchanged behavior on a bare console). An
  unknown value throws an actionable `ConfigException` listing the valid drivers; that
  propagates to `Program.cs` for a clean exit 2 (it is excluded from the crash boundary —
  a typo'd env var is a user error, not a crash).

- **Auto-detect a multiplexer *or Windows Terminal* and default to `dotnet`.** When
  `COBALT_DRIVER` is unset and `ZELLIJ`, `TMUX`, or `WT_SESSION` (set by Windows Terminal) is
  present, `ResolveDriver` selects the `dotnet` driver — so cobalt "just works" there with no
  configuration. Windows Terminal is ANSI-capable, but the Win32-console `windows` driver
  mishandles the `$EDITOR` suspend/resume there too (a blank screen on return) and leaves
  escape codes on exit — the same class of defect, confirmed in UAT. On a bare `conhost`
  console (none of those set) the default is unchanged: `null` → TG picks `windows`.

- **The explicit hatch is the complete backstop.** Auto-detect covers only the multiplexers
  it enumerates (zellij, tmux); screen/ssh/ConEmu/WezTerm-mux users, or anyone the detection
  misses, set `COBALT_DRIVER=dotnet`. And `COBALT_DRIVER` always wins — including
  `COBALT_DRIVER=windows` to force the Win32 driver back inside a multiplexer.

## Consequences

- Multiplexer users need one line of config (`$env:COBALT_DRIVER='dotnet'` /
  `export COBALT_DRIVER=dotnet`), documented in the README and manual-verification guide.
- `ResolveDriver` is pure and unit-tested (unset/blank → null, valid/trim/case →
  canonical, unknown → `ConfigException`); the registry call is guarded by a test that it
  is usable before `Init` and still names the accepted drivers.
- The `dotnet` driver is ANSI/stdio-based; on a real Win32 console the `windows` driver may
  still render or perform better, which is why it stays the default rather than switching
  everyone to `dotnet`.
- Driver selection is now a one-symbol change surface: if a future TG release fixes the
  Win32 driver under a pty, the hatch is simply left unset.
