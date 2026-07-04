# Changelog

## 0.1.0 — 2026-07-04

First release. A vim-flavored Azure DevOps TUI for a neovim-centric workflow.

### Added
- **Auth & config**: Entra ID sign-in (reuse `az login`, else interactive
  browser) with a persistent token cache; multi-context TOML config with fast
  switching (`--context`, `:ctx`); `cobalt auth login` / `auth status`.
- **Work items**: "my work items" list with live filter; detail view with
  HTML-as-Markdown description; state transitions (process-template states),
  assignee/tags/priority/points edits, comments, and description editing.
- **Pull requests**: three tabs (review queue awaiting my vote / mine / active)
  with repo filter; detail with reviewers, votes, merge status, linked work
  items, and comment threads; vote (all five values), reply, resolve/reactivate,
  complete (merge strategy) and abandon — both confirmed.
- **Diff review**: client-side unified diff of a PR's latest iteration with a
  changed-file list, add/remove coloring, thread gutter markers, and
  line-anchored comments (correct left/right sides).
- **Vim layer**: `j/k`, `gg`/`G`, `Ctrl-d`/`Ctrl-u`, `/` filter, `:` command
  palette, `?` help, `yy` yank URL, `gx` open in browser, and contextual verbs.
- Distributed as a dotnet global tool (`cobalt-tui`, command `cobalt`).

### Notes
- Built as a UI-free `Cobalt.Core` + view-model layer (no Terminal.Gui types),
  covered by 189 unit/integration tests. Architecture decisions in `docs/adr/`.
