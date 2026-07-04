# cobalt

A vim-flavored terminal UI for Azure DevOps: work items, pull requests,
reviews, and votes — without leaving your terminal.

Status: **in development** (M0–M3 complete: auth, config, work items). The
agreed specification lives in [docs/SPEC.md](docs/SPEC.md), the milestone plan
in [docs/PLAN.md](docs/PLAN.md), and design decisions in
[docs/adr/](docs/adr/).

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
`$EDITOR` · `a` assign · `t` tags.
