# Changelog

## Unreleased

### Added
- **Width-aware list columns.** PR and work-item rows now size to the terminal:
  fixed columns sit left and the title/summary takes all remaining width, reflowing
  on resize instead of leaving a blank right gutter.
- **PR age column** showing how long ago each PR was created (`45m`/`6h`/`3d`/`5w`).
- **PR project column**, shown only when the list spans more than one project (the
  org-scoped, cross-project case).
- **Lazy comment counts.** After the PR list renders, a capped, cached background
  enricher fetches the non-system comment count for the loaded rows and decorates
  each with a `💬 N` badge as it lands — the list stays instant even when Azure
  DevOps is slow, and a failed or cancelled fetch is silently dropped.
- **Org-wide pull-request scope.** PR lists (review queue / mine / active) now
  default to the **whole organization** instead of a single project. An optional
  per-context `pr_scope = "org" | "project"` config key sets the startup scope
  (default `org`; existing configs stay valid), and `:scope org|project` flips it
  live (bare `:scope` reports the current value). The active scope shows in the
  status line.
- Cross-project drill-in: threads, votes, replies, resolve/complete/abandon, diff
  review, line comments, and `yy`/`gx` web URLs all target each PR's own project,
  so rows spanning projects behave correctly under org scope.
- The PR model now surfaces `ProjectName` and `CreationDate` (groundwork for a
  project column and a PR-age column).

### Notes
- The org-wide PR *list* REST route is not formally documented (the by-id and
  reviewer routes are org-level and documented); it is validated during UAT. See
  ADR 0011.

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
  covered by unit/integration tests. Architecture decisions in `docs/adr/`.

### Deferred to a later release
- Word-level intra-line diff highlighting and syntax highlighting (diffs colour
  whole added/removed lines in v1).
- Rename-aware diffs (a rename currently renders as a whole-file add).
- Driving the terminal suspend/resume around a full-screen `$EDITOR`.
- Cross-iteration PR thread tracking; remappable keybindings.
