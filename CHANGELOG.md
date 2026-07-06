# Changelog

## Unreleased

### Added
- **Structured work-item filters.** The work-item list now follows the org/project
  `:scope` like the PR list — org scope shows items assigned to you across every
  project, project scope only the context project. Two new palette commands narrow
  the lists: `:done show|hide` reveals/hides completed work items (states `Closed`,
  `Done`, `Completed`, `Resolved`, `Removed` — default hides, and `Resolved` is now
  in the excluded set), a server-side WIQL change; and `:project NAME` narrows both
  lists to one project (server-side WIQL `[System.TeamProject]` for work items,
  client-side on `ProjectName` for PRs), with bare `:project` clearing/reporting.
  Active filters show in the work-item header (`my work items (12) · hiding done ·
  project:Fabrikam`); the `/` substring filter composes on top. Org-wide work-item
  querying drops the project segment from the `wiql`/`workitemsbatch` routes (see
  [ADR 0011](docs/adr/0011-cross-project-org-pr-scope.md)).
- **Vim count prefixes.** Motions now take a numeric count everywhere: `5j` moves down
  five rows, `10G` jumps to line 10, `3]` advances three files in diff review, and a
  count multiplies `Ctrl-d`/`Ctrl-u`. A bare `0` stays the line-start motion, not a
  count. Implemented as a pure `KeymapRouter` change (`KeyResult.Count`) threaded through
  the shell and dialogs.
- **Section navigation moved to `g`-chords.** `gt`/`gT` cycle to the next/previous
  section (wrapping) and `g1`/`g2` jump straight to Work Items / Pull Requests. The old
  `1`/`2` section bindings are removed so digits are free for counts; the tab bar now
  reads `[g1:Work Items] [g2:Pull Requests]`.
- **Scrollable, key-aware detail dialogs.** The work-item, PR, and diff-review dialogs
  now route keys through the shared `KeymapRouter`, so `j/k`, `gg`/`G`, and
  `Ctrl-d`/`Ctrl-u` (with counts) scroll the body — in diff review, whichever pane has
  focus. `?` opens a per-view key reference. Diff review gains `]`/`[` (next/prev file,
  count-aware) and `Tab` (cycle panes); PR detail gains `d` diff, `C` complete, `A`
  abandon as first-class bindings. Unclaimed keys still fall through to native widget
  behavior (e.g. `Enter` opens the highlighted diff file). See
  [ADR 0014](docs/adr/0014-shared-count-aware-scroll-seam.md).
- **Act on a row without opening it.** In the work-item list, `c` comment, `s` change
  state, `a` assign, and `t` edit tags now run against the highlighted row; in the PR
  list, `v` votes on the highlighted PR. The flows are the same ones the detail dialogs
  use, extracted into shared `WorkItemActions`/`PrActions` runners (single source of
  truth). A router-matched key that nothing in the current context handles now surfaces
  `'<key>' not available here` instead of being silently swallowed.
- **Crash log + global crash boundary.** An unexpected exception escaping the TUI now
  restores the terminal, writes the full stack to a crash log
  (`$XDG_STATE_HOME/cobalt/crash.log`, `~/.local/state/cobalt/crash.log`, or
  `%LOCALAPPDATA%\cobalt\crash.log`), prints `cobalt crashed — see <path>`, and exits
  non-zero. Background/fire-and-forget task faults route to the same log. See
  [ADR 0013](docs/adr/0013-exception-handling-policy.md).

### Changed
- **`Ctrl-d`/`Ctrl-u` are a true half page everywhere.** In lists they previously did a
  full page (the `ListView` primitive); they now move half the viewport, matching the
  detail dialogs and vim, via the shared `VimScroll` seam.
- **Narrower view-model error handling.** View-model loads/mutations now catch only
  the expected Azure DevOps failure set (API/HTTP/JSON/auth/IO) and surface it in the
  message bar; an unexpected exception propagates to the crash boundary instead of
  being masked as an "error" string (CodeQL `cs/catch-of-all-exceptions`).

### Fixed
- **PR "complete" now reports when the merge is still computing.** Completing a PR
  whose source commit hasn't been resolved yet surfaces a clear message
  ("merge still computing — try again in a moment") instead of silently doing nothing
  and later logging a phantom crash.
- **Dialog verb actions can't fail silently.** Vote/reply/resolve/complete/abandon,
  work-item state/comment/assign/tags/description, and diff line-comments run through a
  shared `FireAndForget.Observe` guard: an unexpected fault is now both recorded to the
  crash log and shown in the message bar immediately, while user cancellation stays
  silent (previously such faults vanished into a discarded task).
- **HTTP timeouts are distinguished from user cancellation.** A request timeout (a
  cancellation not carrying the caller's token) now surfaces as a visible error instead
  of leaving a pane showing "no data"; genuine user/dialog cancellations stay silent.
- **Comment-count enrichment cancels on tab/scope switch.** Switching PR tabs or scope
  now abandons the previous tab's in-flight badge fetches (a fresh per-load token) so
  they no longer hog the fetch semaphore and delay the new tab's badges; a fetch that
  fails is no longer retried on every re-render.
- **Robust crash logging.** A crash-log write that fails (permission denied, malformed
  `$XDG_STATE_HOME`, full disk) can no longer itself crash the app; the boundary falls
  back to printing the stack to stderr, and the unobserved-task handler observes the
  fault before attempting to log it.
- **Tags aren't wiped after a failed load.** Editing tags on a work item that failed to
  load now bails with a message instead of opening an empty editor and replacing the
  server-side tags with nothing.
- PR list selection resets to the top on a tab/scope change (it kept the previous tab's
  row index); same-tab background reloads still restore the reviewer's position.

### Added (earlier in this cycle)
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
