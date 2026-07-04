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
```

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
(`:q` quit, `:ctx NAME` switch context, `:help`, `:messages`) · `?` help ·
`r` refresh. In a work item: `s` state · `c` comment · `e` edit description in
`$EDITOR` · `a` assign · `t` tags. In the PR section: `Tab` cycles the review
queue / mine / active sub-tabs; in a PR: `v` vote · `c` reply · `x` resolve
thread · `u` reactivate · `C` complete · `A` abandon · `d` open diff review.
In diff review: `Tab` switches file list / diff pane · `[`/`]` prev/next file ·
`c` comment on the selected line. Anywhere in a list: `yy` yanks the item's web
URL to the clipboard · `gx` opens it in your browser.

## Known limitations (v1)

- Editing long text via `$EDITOR` from a detail view is wired but the terminal
  hand-off (suspend/resume around a full-screen editor) is not yet driven — see
  the M6 TODO in `CobaltShell`. Single-line edits work in-app.
- Line comments anchor to the latest PR iteration only; the diff is computed
  client-side (see [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md)).
- Diffs colour whole added/removed lines; word-level intra-line highlighting and
  syntax highlighting are deferred to a later release. Renames currently render
  as a whole-file add.
- Keybindings are fixed in v1; a remapping config is post-v1.

## Development

```sh
dotnet test Cobalt.slnx      # 189 tests
dotnet build Cobalt.slnx
dotnet run --project src/Cobalt -- --help
```
