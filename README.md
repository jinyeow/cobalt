# cobalt

A vim-flavored terminal UI for Azure DevOps: work items, pull requests,
reviews, and votes — without leaving your terminal.

Status: **v0.1.0** — feature-complete for the agreed v1 (work items, pull
requests, diff review with line comments). The agreed specification lives in
[docs/SPEC.md](docs/SPEC.md), the milestone plan in [docs/PLAN.md](docs/PLAN.md),
and design decisions in [docs/adr/](docs/adr/).

Built with a UI-free `Cobalt.Core` and a view-model layer that never references
Terminal.Gui, so the interesting logic is unit-tested (386 tests). See
[docs/adr/0004](docs/adr/0004-terminal-gui-v2-with-viewmodels.md) and
[0007](docs/adr/0007-vim-input-as-testable-data.md).

## Install

```sh
dotnet tool install -g cobalt-tui   # command is `cobalt`
```

## Configure

Create `~/.config/cobalt/config.toml` (XDG-aware; `%APPDATA%` on Windows):

```toml
default_context = "work"
# theme = "dark"       # optional: "dark" (default) · "light" · "system" (follow the OS).
                       # Switch live with :theme; system-follow is Windows-only for now.

[contexts.work]
organization = "https://dev.azure.com/YOUR_ORG"   # or a bare "YOUR_ORG"
project = "YOUR_PROJECT"
# pr_scope = "org"     # optional: "org" (default — PRs across the whole org)
                       # or "project" (only this project). Toggle live with :scope.
```

Both the pull-request and work-item lists default to the **whole organization**
(org scope shows work items assigned to you across every project). Set `pr_scope =
"project"` to start focused on one project, or flip either way at runtime with
`:scope org` / `:scope project` (bare `:scope` shows the current value). The
active scope appears in the status line.

Two more runtime filters narrow the lists:

- `:done show` / `:done hide` reveals or hides completed work items (states
  `Closed`, `Done`, `Completed`, `Resolved`, `Removed`); the default hides them.
  Bare `:done` reports the current setting. This is a server-side WIQL change.
- `:project NAME` narrows both lists to a single project (server-side for work
  items, client-side for PRs); bare `:project` clears an active filter or reports
  that none is set. Active `:done` / `:project` filters show in the work-item
  list header. The `/` substring filter still composes on top.

## Themes

`theme` picks the colours: `dark` (default — the original look), `light`, or `system`
(follow the OS light/dark setting). Switch live with `:theme dark|light|system` (bare
`:theme` reports the current one). Cobalt uses Terminal.Gui's built-in themes for the app
chrome and syntax highlighting, and its own palette for the diff tints, so both recolour
together. `system` follows the OS live on **Windows** (via the light/dark registry setting);
on macOS/Linux it falls back to `dark` for now (use `:theme` to switch manually). See
[ADR 0019](docs/adr/0019-hybrid-theming.md).

## Sign in

Cobalt uses Entra ID (no PATs). It reuses an existing `az login` session, or
falls back to an interactive browser sign-in:

```sh
cobalt auth login     # interactive sign-in
cobalt auth status    # who am I, per context
cobalt                # launch the TUI
cobalt --context oss  # launch against a named context
```

## Keys (vim layer)

The bottom row is an always-visible **keybar** showing the keys for the current
context (generated from the live binding table, so it never drifts), and the status
row shows an armed count or pending chord vim-showcmd-style (`5g` while you type
`5gg`). The tab strip names its jump chords, and the PR sub-tabs render as a real
tab row with the active tab highlighted.

`j/k` move · `gg`/`G` top/bottom · `Ctrl-d`/`Ctrl-u` half-page · `/` filter ·
`Enter`/`o`/`l` open · `h`/`q` back/close · `gt`/`gT` next/prev section · `g1`/`g2` jump to Work Items /
Pull Requests · `Tab` next tab · `:` command palette
(`:q` quit, `:context NAME` switch context, `:scope org|project` list breadth,
`:done show|hide` completed work items, `:project NAME` narrow to one project,
`:help`, `:messages`) · `?` help ·
`r` refresh. On a work item (the highlighted list row or its detail): `s` state ·
`c` comment · `a` assign · `t` tags; the detail additionally has `e` edit
description in `$EDITOR`. In the PR section: `[`/`]` (or `Tab`) cycle the review queue / team /
mine / active sub-tabs; on a PR (the highlighted list row or its detail): `v` vote; the
detail additionally has `c` reply · `g c` add a PR-level comment · `g b` open the source
branch in the browser · `x` resolve thread · `u` reactivate · `C` complete · `A` abandon ·
`d` open diff review, and shows the PR's **branch-policy / build status** (pass/fail, blocking).
In diff review (vim `]`/`[` bracket motions, all count-aware): `Tab` switches file list /
diff pane · `]f`/`[f` next/prev file · `]c`/`[c` next/prev **change hunk** · `]t`/`[t`
next/prev **comment thread** · `]v`/`[v` next/prev **unviewed** file. `z` collapses/expands
the folder under the cursor (`Enter` on a folder also toggles) · `m`/`M` mark the current file
**viewed**/**unviewed** (a `[✓]`/`[ ]` column in the tree). `s` switches the diff between **unified** and
**side-by-side**; unchanged context is **folded** by default — `e` expands a fold, `E` the
whole file. `/` opens an **inline search** of the current file (type, `Enter` to search,
`n`/`N` jump between matches, `Esc` cancels). `h`/`l` (or `←`/`→`) **scroll horizontally** for
long lines — so inside diff review those keys scroll rather than back/open, and you close the
review with `q`/`Esc`. `c` comments on the selected line; `o`/`Enter` opens the existing
comment **thread(s)** on that line, where `c` replies, `x` resolves, `u` reactivates. `v`
votes on the PR; `g b` opens the PR's **source branch** in the browser; `T` filters the file
list to files with **unresolved threads** (the header shows the unresolved count). Each file
row shows its `+added −deleted` count (with a PR total in the header) as diffs load in the
background.
The changed-file list is a **directory tree** — files group under their folder and the
distinguishing filename always shows in full, instead of a flat, left-truncated path.
The layout is **responsive**: on a narrow terminal the file list hides so the diff keeps the
full width, and side-by-side falls back to unified when there isn't room for two columns.
Anywhere in a list: `yy` yanks the item's web
URL to the clipboard · `gx` opens it in your browser.

**Count prefixes.** Motions take a numeric count, vim-style: `5j` moves down five
rows, `10G` jumps to line 10, `3]f` advances three files in diff review, and a count
multiplies `Ctrl-d`/`Ctrl-u`. Digits are reserved for counts, which is why sections
moved off `1`/`2` and onto the `gt`/`gT`/`g1`/`g2` chords.

**In-view scrolling.** The detail dialogs (work item, PR, diff review) and the `?`/
`:messages` overlays are scrollable with the same keys — `j/k`, `gg`/`G`,
`Ctrl-d`/`Ctrl-u` (with counts) — and `?` shows the key reference for the current
view. In diff review those keys scroll whichever pane (file list or diff) has focus.

## Lists

Rows are width-aware: fixed columns sit left and the title/summary takes all the
remaining terminal width (reflowing on resize), so there's no dead right gutter.

- **Pull requests**: id · vote summary · **age** (`45m`/`6h`/`3d`/`5w` since the PR
  was created) · a **project** column when the list spans more than one project
  (org scope) · repository · title. A **comment count** badge (`💬 3`) fills in
  **lazily in the background** after the list renders — the list stays instant even
  when Azure DevOps is slow, and counts are fetched only for loaded rows, capped and
  cached. The **team** sub-tab shows the union of PRs where a team you belong to is a
  requested reviewer and PRs a teammate authored (deduped, newest first; see
  [ADR 0015](docs/adr/0015-team-pr-view.md)).
- **Work items**: id · type · state · a **project** column when the list spans more
  than one project (org scope) · title (fills) · iteration · changed date. Acting on a
  row (`s`/`c`/`a`/`t`/open) targets that item's own project, so cross-project drill-in
  stays correct under org scope.

## Editor

**Comments and replies** are typed in a fast **in-TUI editor** — no terminal handoff, so it's
instant (see [ADR 0020](docs/adr/0020-in-tui-text-input.md)). **Enter** submits, **Esc** cancels,
**Ctrl-J** (or Ctrl-Enter where your terminal delivers it) inserts a newline, and **Ctrl-E** hands
the current text to your `$EDITOR` for heavier editing, then drops you back to review before you
submit. Short prompts — a thread id, an assignee — are one-line fields where Enter submits.

**Editing a work-item description**, and the `Ctrl-E` escape hatch, still open your editor on a
temp file (the git-commit pattern). cobalt resolves it in this order:

1. `$VISUAL`
2. `$EDITOR`
3. `vi` (fallback)

The value may include arguments and quoted paths (e.g. `EDITOR="code --wait"`).
GUI editors must be told to block until the file is closed (`code --wait`,
`gvim -f`), or the edit is read back before you finish.

Set it in the shell you launch cobalt from:

- **bash/zsh**: `export EDITOR=nvim` (add to your profile to persist)
- **PowerShell**: `$env:EDITOR = 'nvim'` for the session, or `setx EDITOR nvim`
  to persist (then open a **new** terminal), or set it under Windows Settings →
  Environment Variables.
- **cmd**: `set EDITOR=nvim` for the session.

If neither variable is set, description editing and the `Ctrl-E` hatch fall back to `vi`; if that
isn't installed you'll see *"could not start editor … set $VISUAL or $EDITOR"*.
A full-screen editor gets a clean terminal (the TUI suspends while it runs).

## Terminal multiplexers (zellij / tmux)

Inside a multiplexer, cobalt runs against a pseudo-terminal rather than a real Win32
console, and Terminal.Gui's default `windows` driver (Win32 console APIs) drops
keystrokes and breaks the `$EDITOR` handoff there. cobalt **auto-detects zellij and
tmux** (`ZELLIJ`/`TMUX`) and switches to the stdio/ANSI `dotnet` driver, so it works
there with no configuration.

For any other multiplexer, or to override, set `COBALT_DRIVER`:

```sh
export COBALT_DRIVER=dotnet     # bash/zsh
$env:COBALT_DRIVER = 'dotnet'   # PowerShell
```

Accepted values are `windows`, `dotnet`, and `ansi`; an explicit value always wins
(e.g. `COBALT_DRIVER=windows` forces the Win32 driver back). On a bare console, leave
it unset. See [ADR 0016](docs/adr/0016-terminal-driver-selection.md).

## Crash logs

Expected Azure DevOps failures (auth, network, API) surface in the message bar. An
*unexpected* error restores your terminal, appends the full stack to a crash log, and
exits non-zero after printing `cobalt crashed — see <path>`. The log lives in the XDG
state dir: `$XDG_STATE_HOME/cobalt/crash.log` (default `~/.local/state/cobalt/crash.log`),
or `%LOCALAPPDATA%\cobalt\crash.log` on Windows. See
[ADR 0013](docs/adr/0013-exception-handling-policy.md).

## Known limitations

- Diffs colour changed *words* (intra-line) and code tokens for C#/JS-TS/JSON/
  Python; syntax highlighting is **line-local**, so a cross-line block comment or
  triple-quoted string is tokenised per line. Wide characters (CJK/emoji) can make
  the full-width diff tint stop short of the row edge — cosmetic, see
  [ADR 0010](docs/adr/0010-diff-pane-colored-listview-data-source.md).
- Line comments anchor to the latest PR iteration only; the diff is computed
  client-side (see [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md)).
- `$EDITOR` needs a real interactive terminal (it suspends Terminal.Gui and hands
  over the tty — see [ADR 0009](docs/adr/0009-editor-suspend-resume.md)).
- `:context` switching updates the status bar but does not yet reconnect the data
  screens to the new org/project (restart with `--context` for now).
- Keybindings are fixed; a remapping config is post-v1.

## Development

```sh
dotnet test Cobalt.slnx      # unit + fuzz + integration tests
dotnet build Cobalt.slnx
dotnet run --project src/Cobalt -- --help
```

### Testing local changes

**Run straight from source (recommended).** This always compiles the current code — no
packaging, no tool cache, no version confusion, so it's the surest way to test a change:

```sh
dotnet run --project src/Cobalt -c Release -- auth login
dotnet run --project src/Cobalt -c Release -- auth status
dotnet run --project src/Cobalt -c Release            # launch the TUI
```

**Or install your build as the `cobalt` global tool.** **Watch the cache:** the package
version is fixed (`0.2.0`) and NuGet caches packages *by version*, so `dotnet tool
update`/`install --add-source` can silently reuse a **stale** cached build — which looks
exactly like "my change didn't take". Give each build a unique version so the install
can't hit the cache:

```sh
dotnet tool uninstall -g cobalt-tui
dotnet pack src/Cobalt/Cobalt.csproj -c Release -o ./artifacts \
  -p:PackageVersion=0.2.0-local1 -p:PublishRepositoryUrl=false -p:EmbedUntrackedSources=false
dotnet tool install -g cobalt-tui --add-source ./artifacts --version 0.2.0-local1
```

Bump `local1` → `local2` … on each rebuild. To keep the same version instead, delete the
cached package first: `rm -rf ~/.nuget/packages/cobalt-tui` (Windows:
`rmdir /s /q "%USERPROFILE%\.nuget\packages\cobalt-tui"`). Note `cobalt --version` prints
only `0.2.0` — it can't tell you which commit you're on; use `git log --oneline -1` for that.

### Before pushing

Run the clean, CI-style build — analyzer errors (e.g. unused `using`s) fire only on a
*clean* build, so an incremental `dotnet build` can pass while CI fails:

```sh
dotnet clean Cobalt.slnx -c Release
dotnet build Cobalt.slnx -c Release -p:ContinuousIntegrationBuild=true --no-incremental  # 0 warnings
dotnet test Cobalt.slnx
```
