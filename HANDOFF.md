# cobalt — session handoff

A working handoff for continuing cobalt in a **local terminal Claude Code session**.
The main reason to move here: this repo's remote build sessions can't run the TUI
against a real TTY or a real Azure DevOps org — a terminal session can. Your
highest-value job here is to **drive and test the actual TUI** and close the
open UAT items below.

## What cobalt is

A vim-flavored terminal UI (Terminal.Gui v2) for Azure DevOps — browse and act on
**work items** and **pull requests** (view/edit, review/comment/vote, diff review)
from the terminal, with vim keybindings. .NET 10, latest C#, MVVM (UI-free
`Cobalt.Core` + `Cobalt.Tui`).

## Current state (as of this handoff)

- **Branch:** `claude/azure-devops-tui-97d16e` (all work lives here; there is an open PR into `main`).
- **Tests:** 518 green (`dotnet test`). **CI:** green across ubuntu/windows/macos + CodeQL.
- **Design record:** `docs/adr/0001`–`0015`, `docs/plans/`, `docs/SPEC.md`, `docs/PLAN.md`, `CHANGELOG.md`. Read the ADRs first — they capture every non-obvious decision.
- Round-1 delivered the v1 (M0–M6) + `$EDITOR` suspend/resume + rich diff review. Round-2 (this session) added: vim **count prefixes**, a shared **in-view scroll** seam (`j/k/gg/G/Ctrl-d/u` + `?` help in every dialog), **org/cross-project scope** for PRs *and* work items, adaptive columns + PR age + lazy comment badges, a **Team** PR tab, structured **work-item filters** (`:done`/`:project`), plus editor-resume and double-press fixes. Two review passes (fable) gated each; a HIGH cross-project-routing regression was caught and fixed.

## Run it (the part the remote session couldn't do)

Prereqs: **.NET 10 SDK**, a terminal (real TTY), and an authenticated Azure DevOps org.

```sh
# 1. build + install the tool from this branch (not on nuget.org)
git fetch origin claude/azure-devops-tui-97d16e && git checkout claude/azure-devops-tui-97d16e && git pull
dotnet pack src/Cobalt/Cobalt.csproj -c Release -o ./artifacts
dotnet tool update -g cobalt-tui --add-source ./artifacts --version 0.1.0   # or uninstall+install
dotnet tool list -g | grep cobalt        # confirm the fresh build; ensure ~/.dotnet/tools is on PATH

# 2. config: ~/.config/cobalt/config.toml  (%APPDATA% on Windows)
#    default_context = "work"
#    [contexts.work]
#    organization = "https://dev.azure.com/YOUR_ORG"
#    project      = "YOUR_PROJECT"
#    # pr_scope   = "org"   # default; or "project"

# 3. auth (Entra ID, no PATs — reuses az login or interactive browser)
cobalt auth login
cobalt auth status        # who am I, per context

# 4. set your editor (comment/edit actions open $EDITOR on a temp file)
export EDITOR=nvim        # PowerShell: $env:EDITOR='nvim' ; then run cobalt from that shell

# 5. launch
cobalt                    # or: cobalt --context oss
```

## UAT checklist — drive these in a real terminal

**Priority 1 — flagged "can't verify without a real terminal / real org" (verify first):**

- [ ] **Double-input / responsiveness** — a single `j`/`k` moves once and repaints immediately (no "press twice"). The fix forces a full redraw after nav; confirm on Windows especially.
- [ ] **Editor resume** — `c` (comment) → edit in `$EDITOR` → `:wq`: on return the dialog still takes keys (`q/e/s/t/c`) **and** there are no stray escape codes in the corner. (Fix: `ClearContents` + full redraw + refocus `TopRunnableView`.)
- [ ] **Org-wide work items** (`:scope org`, the default) — the WI list shows items assigned to you **across all projects**, and a **project column** appears when rows span >1 project. This hits a project-segment-less `_apis/wit/wiql` + `workitemsbatch` route only verified here by shape.
- [ ] **Cross-project WI drill-in** (the round-2 HIGH fix) — with context project A, open/act on an item from project B (`s` change-state, edit `e`, comment `c`): it must use **B's** metadata (correct states, no 404), not A's.
- [ ] **Team PR tab** — the new tab between "review queue" and "mine": shows the raw union of PRs where a team you're on is a reviewer + PRs authored by teammates. Depends on `_apis/teams?$mine=true` and `reviewerId={teamGuid}` (documented-by-example).

**Priority 2 — vim layer & filters (mechanics, verify feel):**

- [ ] Counts: `5j`, `3k`, `15G`, `5gg` (goto line); `0` is currently a no-op (not line-start).
- [ ] Sections: `gt`/`gT` cycle, `g1`/`g2` jump; `1`/`2` are no longer section keys (they're count digits now).
- [ ] In-view scrolling: `j/k/gg/G/Ctrl-d/Ctrl-u` scroll the PR/work-item **description** and the **diff** panes; `?` opens a key-reference overlay in every dialog (and the overlay itself scrolls).
- [ ] Diff review: `]`/`[` next/prev file, `Tab` cycles panes, `c` line comment.
- [ ] Filters: `:done show|hide`, `:project NAME` (both lists), `:scope org|project`, `:context NAME`; `/` substring filter composes on top.
- [ ] `q` quits from the main view; `:q` also quits; `Esc` clears a pending count/sequence (never quits).
- [ ] `yy` yank URL / `gx` open-in-browser use the item's own project under org scope.

Note what breaks with concrete repro steps; fix test-first and re-run the loop below.

## House rules (keep these — they're why CI stays green and history stays clean)

- **TDD, always** — failing test first, then the fix. The suite is the safety net for the many headless view-level behaviors.
- **The IDE0005 trap** — unused-using errors only fire on a *clean* CI build, not local incremental builds. Before every push run:
  `dotnet clean Cobalt.slnx -c Release && dotnet build Cobalt.slnx -c Release -p:ContinuousIntegrationBuild=true --no-incremental` (must be **0 errors**), then `dotnet test`.
- **Terminal.Gui 2.4.16** — don't guess its API; decompile/probe the package (`~/.nuget/packages/terminal.gui/2.4.16/...`) or write a headless probe. Known gotchas: `TextView.Viewport.Y` is an unreliable scroll offset (assert `CurrentRow`); use `View.InvokeCommand(Command.*)` for uniform scrolling; view trees route keys headlessly with no driver/`Init()`.
- **Commits** — no AI/assistant/"Generated by" references anywhere; author as **Justin Puah `<justin@puah.dev>`** (`git config user.name/user.email` are already set on this branch). Checkpoint green-then-push per logical unit.
- **Docs** — update the relevant ADR + README + CHANGELOG with any behavior change; add the next-numbered ADR for architectural decisions.
- Work only on `claude/azure-devops-tui-97d16e`.

## Architecture map

- `src/Cobalt.Core` — UI-free: models, ADO REST clients (`Ado/`), config, text/diff. No Terminal.Gui types (ADR 0004). System.Text.Json source-gen.
- `src/Cobalt.Tui` — Terminal.Gui shell (`App/CobaltShell.cs`), view-models (`ViewModels/`), screens/dialogs (`Screens/`), the vim input layer (`Input/` — `KeymapRouter`, `KeyBindingTable`, `KeyTokenizer`; ADR 0007), the shared `VimScroll` seam (ADR 0014).
- `src/Cobalt` — the `cobalt` exe/tool + `auth` verbs.
- `tests/` — `Cobalt.Core.Tests`, `Cobalt.Tui.Tests` (xunit v3; async APIs need `TestContext.Current.CancellationToken`). Dialogs are tested headlessly by driving `dialog.NewKeyDownEvent(...)` (see `DetailDialogKeyDeliveryTests`).

## Known limitations & open threads

- **`$top=200` truncation** — the teammate-authored half of the Team tab and the teams list itself cap at 200 (paging deferred; ADR 0015).
- **Undocumented-by-example ADO routes** (flagged in ADRs 0011/0015): org-wide PR/WI list routes and `reviewerId={teamGuid}`. If a UAT shows one misbehaves, the code is structured so swapping to a documented per-project fan-out is localized.
- **Repo hardening not yet applied** (needs GitHub UI, no API here): branch protection on `main` (require PR + the 3 CI jobs + CodeQL, no force-push), and dismissing the handful of *intentional* `catch (Exception)` CodeQL notes (crash boundary, `FireAndForget`, `UiThreadSuspender`, top-level `app.Run`) via Security → Code scanning → "Won't fix".
- `0` (no count) is a no-op, not vim's line-start motion — implement `Command.LeftStart` if wanted.
- A vim scroll on lists makes `Ctrl-d/u` a true half-page everywhere (was full-page on lists in round-1).

## The loop to keep using

1. Reproduce a UAT finding with concrete steps.
2. Write the failing test at the right layer (router/VM/pure logic → unit; dialog/scroll → headless view-level).
3. Fix; run the clean CI-style build + `dotnet test`.
4. Checkpoint (green) → push. For substantial batches, an adversarial review pass before finalizing has paid off twice.
