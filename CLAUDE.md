# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What cobalt is

A vim-flavored terminal UI (Terminal.Gui v2) for Azure DevOps — browse and act on **work
items** and **pull requests** (view/edit, review/comment/vote, client-side diff review)
from the terminal, with dark/light/system theming. .NET 10, latest C#, MVVM. Ships as the `cobalt` .NET global tool
(`cobalt-tui` package). Auth is Entra ID only (no PATs), reusing an `az login` session or
an interactive browser sign-in.

## Commands

```sh
dotnet build Cobalt.slnx                          # build all (src + tests)
dotnet test  Cobalt.slnx                          # full suite (~750 tests)
dotnet run --project src/Cobalt -- --help         # run the CLI/TUI

# run a single test / class / trait
dotnet test Cobalt.slnx --filter "FullyQualifiedName~KeymapRouter"
dotnet test Cobalt.slnx --filter "DisplayName~yanks the item"
```

**Before every push, run the clean CI-style build — not an incremental one:**

```sh
dotnet clean Cobalt.slnx -c Release
dotnet build Cobalt.slnx -c Release -p:ContinuousIntegrationBuild=true --no-incremental   # must be 0 errors
dotnet test  Cobalt.slnx -c Release
```

`TreatWarningsAsErrors=true`, and **IDE0005 (unused usings) only fires on a *clean* build**,
never on a local incremental one — so an incremental build can pass while CI fails. CI
(`.github/workflows/ci.yml`) builds Release with `ContinuousIntegrationBuild=true` across
ubuntu/windows/macos, then packs. There is also CodeQL (`codeql.yml`) and release
(`release.yml`, tag-driven).

## Architecture — the three-project layering (ADR 0004)

The core rule that spans the codebase: **UI logic never touches Terminal.Gui.**

- **`src/Cobalt.Core`** — UI-free library: models, config, text/diff, and the hand-rolled
  Azure DevOps REST client (`Ado/`). No Terminal.Gui types. Serialization is
  `System.Text.Json` source-gen (AOT-friendly). See ADR 0002.
- **`src/Cobalt.Tui`** — the Terminal.Gui shell. Split so that:
  - `ViewModels/` hold all screen state, commands, and formatting and reference **only**
    `Cobalt.Core` — never Terminal.Gui. This is where testable logic lives.
  - `Screens/` are thin bindings from view-model state to widgets.
  - `Input/` is the vim layer as pure data (see below).
- **`src/Cobalt`** — the `cobalt` exe/global tool: `Program.cs` (arg parsing + top-level
  error boundary) and `AuthCommands.cs` (`auth login`/`auth status`). `CobaltTuiApp.Run`
  in `Cobalt.Tui` is the composition root that wires the ADO clients, view-models, and
  shell together.

Consequence to respect: put behavior in a view-model (unit-testable in CI) rather than in
a `Screens/` view whenever you can. Terminal.Gui upgrades should touch only `Cobalt.Tui`.

## The vim input layer (ADR 0007)

Modeled as pure data + pure functions so the product's core feel is fully tested without a
terminal. The chain is `Key → KeyTokenizer → KeymapRouter → ShellViewModel`:

- `KeyBindingTable` — `(token-sequence, command)` bindings per `KeyScope`; the single edit
  point for a future remapping config.
- `KeymapRouter` — stateful sequencer over string tokens (`"j"`, `"C-d"`, `"Esc"`),
  returning Matched/Pending/None, count-prefix aware.
- `KeyTokenizer` — the **only** place a Terminal.Gui `Key` becomes one of our tokens.
- `PaletteCommandParser` / `ShellViewModel` — `:`-command parsing and dispatch, pure.

Add or change a keybinding in `KeyBindingTable`; add its handling in the view-model — not
in a view.

## Text entry & theming

- **Text entry** goes through the UI-free `ITextInput.ReadAsync` seam (ADR 0020): short,
  frequent entry (line/PR comments, replies, thread-id + assignee prompts) uses an in-TUI
  `TextView`/`TextField` dialog — **Enter submits, Ctrl-J / Ctrl-Enter inserts a newline, Esc
  cancels, Ctrl-E hands off to `$EDITOR`**. Long-form (work-item descriptions, tags for now)
  still opens `$EDITOR` via the suspend/resume handoff (ADR 0009). The in-TUI path is
  headless-testable; the handoff is UAT-only.
- **Theming** is hybrid (ADR 0019): Terminal.Gui themes the chrome + syntax roles, while the
  diff tints are a cobalt-owned `DiffPalette` that `DiffListDataSource` reads from the ambient
  `ThemeService.CurrentPalette` on each render — don't hard-code diff colours. `theme =
  dark|light|system` in `config.toml` (default `dark`); `:theme dark|light|system` switches live.

## Testing (TDD is the house rule)

xunit v3. The suite is the safety net for many headless view-level behaviors, so work
**test-first** (failing test → fix). Layer the test to the code:

- Router / view-model / pure logic → plain unit test.
- Dialog / scroll behavior → headless view-level test that drives
  `dialog.NewKeyDownEvent(...)` (view trees route keys headlessly with no driver/`Init()`).
  See `DetailDialogKeyDeliveryTests`.
- Async xunit APIs need `TestContext.Current.CancellationToken`.

`tools/uat` is a separate console harness (deliberately **not** in `Cobalt.slnx`) that hits
**live** Azure DevOps routes the headless CI build can only verify "by shape" (org-wide
WI/PR list routes, cross-project drill-in, `reviewerId={teamGuid}`). Run it in a real
terminal against a real org: `dotnet run --project tools/uat -- --context <name>`.

## Terminal.Gui gotchas (pinned 2.4.16)

Don't guess its API — decompile/probe the package (`~/.nuget/packages/terminal.gui/2.4.16/`)
or write a headless probe. Known traps:

- `TextView.Viewport.Y` is an unreliable scroll offset — assert `CurrentRow` instead.
- Use `View.InvokeCommand(Command.*)` for uniform scrolling.
- **Driver selection**: the Win32-console `windows` driver drops keystrokes and breaks the
  `$EDITOR` handoff through a multiplexer's pty. `CobaltTuiApp.ResolveDriver` auto-detects
  zellij/tmux (`ZELLIJ`/`TMUX`) → `dotnet` driver; `COBALT_DRIVER` overrides. See ADR 0016.

## Conventions

- **ADRs are the source of truth for non-obvious decisions** — read `docs/adr/` first
  (`docs/adr/README.md` indexes them). Any behavior change updates the relevant ADR +
  README + CHANGELOG; a new architectural decision gets the next-numbered ADR.
- Nullable enabled, `latest` C#, file-scoped namespaces, 4-space indent (2 for
  config/markup); see `.editorconfig`.
- Central package management — all versions in `Directory.Packages.props`; shared build
  props in `Directory.Build.props`.
- Exception policy (ADR 0013): expected ADO failures (auth/network/API) surface in the
  message bar; an *unexpected* escape restores the terminal, appends to a crash log, and
  exits non-zero. The intentional `catch (Exception)` crash boundaries live in
  `CobaltTuiApp`, `FireAndForget`, and the top-level `app.Run`.
- Commits: conventional commits; no AI/assistant/"Generated by" references anywhere.
