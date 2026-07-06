# cobalt

A vim-flavored terminal UI for Azure DevOps: work items, pull requests,
reviews, and votes — without leaving your terminal.

Status: **v0.1.0** — feature-complete for the agreed v1 (work items, pull
requests, diff review with line comments). The agreed specification lives in
[docs/SPEC.md](docs/SPEC.md), the milestone plan in [docs/PLAN.md](docs/PLAN.md),
and design decisions in [docs/adr/](docs/adr/).

Built with a UI-free `Cobalt.Core` and a view-model layer that never references
Terminal.Gui, so the interesting logic is unit-tested (189 tests). See
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

[contexts.work]
organization = "https://dev.azure.com/YOUR_ORG"   # or a bare "YOUR_ORG"
project = "YOUR_PROJECT"
# pr_scope = "org"     # optional: "org" (default — PRs across the whole org)
                       # or "project" (only this project). Toggle live with :scope.
```

Pull-request lists default to the **whole organization**. Set `pr_scope =
"project"` to start focused on one project, or flip either way at runtime with
`:scope org` / `:scope project` (bare `:scope` shows the current value). The
active scope appears in the status line.

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

`j/k` move · `gg`/`G` top/bottom · `Ctrl-d`/`Ctrl-u` half-page · `/` filter ·
`Enter`/`o` open · `1`/`2` sections · `Tab` next tab · `:` command palette
(`:q` quit, `:ctx NAME` switch context, `:scope org|project` PR breadth,
`:help`, `:messages`) · `?` help ·
`r` refresh. In a work item: `s` state · `c` comment · `e` edit description in
`$EDITOR` · `a` assign · `t` tags. In the PR section: `Tab` cycles the review
queue / mine / active sub-tabs; in a PR: `v` vote · `c` reply · `x` resolve
thread · `u` reactivate · `C` complete · `A` abandon · `d` open diff review.
In diff review: `Tab` switches file list / diff pane · `[`/`]` prev/next file ·
`c` comment on the selected line. Anywhere in a list: `yy` yanks the item's web
URL to the clipboard · `gx` opens it in your browser.

## Lists

Rows are width-aware: fixed columns sit left and the title/summary takes all the
remaining terminal width (reflowing on resize), so there's no dead right gutter.

- **Pull requests**: id · vote summary · **age** (`45m`/`6h`/`3d`/`5w` since the PR
  was created) · a **project** column when the list spans more than one project
  (org scope) · repository · title. A **comment count** badge (`💬 3`) fills in
  **lazily in the background** after the list renders — the list stays instant even
  when Azure DevOps is slow, and counts are fetched only for loaded rows, capped and
  cached.
- **Work items**: id · type · state · title (fills) · iteration · changed date.

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
- `:ctx` switching updates the status bar but does not yet reconnect the data
  screens to the new org/project (restart with `--context` for now).
- Keybindings are fixed; a remapping config is post-v1.

## Development

```sh
dotnet test Cobalt.slnx      # unit + fuzz + integration tests
dotnet build Cobalt.slnx
dotnet run --project src/Cobalt -- --help
```
