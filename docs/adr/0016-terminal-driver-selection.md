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

- **Auto-detect a multiplexer and default to `dotnet`.** When `COBALT_DRIVER` is unset and
  `ZELLIJ` or `TMUX` is present in the environment, `ResolveDriver` selects the `dotnet`
  driver — so cobalt "just works" under the common multiplexers with no configuration. On a
  bare console (neither var set) the default is unchanged: `null` → TG picks `windows`.

- **Auto-detect a remote/RDP session and default to `dotnet` (amended 2026-07-16).** When
  `COBALT_DRIVER` is unset and `SESSIONNAME` starts with `RDP-` (e.g. `RDP-Tcp#0`), select
  `dotnet`. On the `windows` driver a remote session paints via the Win32 console API, and
  ConPTY must diff that console buffer and re-encode it as VT before the terminal — which
  renders in software on a GPU-less host — draws it; over a latency link that translation
  dominates, and the terminal process (not cobalt) becomes the top CPU consumer while cobalt
  itself sits near 0%. Confirmed on a Windows 365 Cloud PC (2 vCPU / 16 GB, no GPU): with
  the default driver the terminal ran ~30–44% CPU navigating a diff; `COBALT_DRIVER=dotnet`
  dropped it sharply and the UI became responsive. The `dotnet` driver writes VT straight to
  stdout, skipping the console round-trip. A physical console (`SESSIONNAME=Console`) is
  unchanged: `null` → `windows`.

- **The explicit hatch is the complete backstop.** Auto-detect covers only the multiplexers
  it enumerates (zellij, tmux); screen/ssh/ConEmu/WezTerm-mux users, or anyone the detection
  misses, set `COBALT_DRIVER=dotnet`. And `COBALT_DRIVER` always wins — including
  `COBALT_DRIVER=windows` to force the Win32 driver back inside a multiplexer.

- **Pin the platform default explicitly; never fall through to TG auto-detect (amended
  2026-07-22).** Terminal.Gui 2.4.17 added an `ansi` driver, and its auto-detect now selects
  it (verified empirically: `IDriver.GetName()` reports `ansi` after `Init(null)` on a bare
  Windows Terminal — the "`null` → TG picks `windows`" claims above describe 2.4.16 and are
  superseded). The ansi driver's input path drops every other keypress: vim `j`/`k` moves
  once per two presses (the second press moves 1 row, both lists, persistent — input eaten,
  not a repaint lag). Reproduces identically on `main` and on feature branches under
  `COBALT_DRIVER=ansi`, while `windows` and `dotnet` are clean, so it is the driver, not
  cobalt logic. `ResolveDriver` therefore pins the pre-2.4.17 platform default explicitly
  (`windows` on Windows, `dotnet` elsewhere) when no override and no multiplexer/RDP
  detection applies — and the multiplexer/RDP path degrades to that same pin (never to TG
  auto) if the `dotnet` driver is ever unregistered. `null` (TG picks) remains only as the
  last resort when even the pinned driver is missing. `COBALT_DRIVER=ansi` still passes
  through for retesting the upstream bug.

### Targeted redraw on vim movement (round 2, INPUT-1) — both-driver UAT passed

The earlier `LayoutAndDraw(false)→(true)` change (`fb5b777`, see Context above) forced a full
`Application.LayoutAndDraw(true)` on every vim move to dodge a driver dirty-flag quirk, even
though only the moved list actually changed. Round 2 replaces that with a targeted repaint: the
moved list view calls `SetNeedsDraw()` on itself, and the app then runs
`Application.LayoutAndDraw(false)` — no forced full-app repaint — relying on the explicit dirty
flag to cover what `force:true` was compensating for on a programmatic `InvokeCommand` move.

**UAT passed 2026-07-22** on both the `windows` and `dotnet` drivers: vim `j`/`k` movement
paints correctly on the first press with INPUT-1's targeted redraw in place. (The double-press
observed at that UAT was the 2.4.17 `ansi` auto-detect driver — see the 2026-07-22 pin above —
initially misattributed to INPUT-1 precisely because the symptom matched the original
`fb5b777`-era report. INPUT-1 is exonerated.)

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
