# Changelog

## Unreleased

### Added
- **Auto-switch to the `dotnet` driver in remote/RDP sessions.** cobalt now detects a remote
  session (`SESSIONNAME=RDP-*`, e.g. a Windows 365 Cloud PC), like it already does for
  zellij/tmux, and selects the stdio/ANSI `dotnet` driver instead of the Win32 `windows`
  driver. On a remote, GPU-less host the `windows` driver's console painting is translated to
  VT by ConPTY on every redraw — expensive over a latency link, and it drives the terminal
  process's CPU high while cobalt itself stays near 0%. The `dotnet` driver writes VT straight
  to stdout and skips the round-trip, so navigation stays responsive with no configuration. A
  physical console is unchanged; `COBALT_DRIVER` still overrides. See
  [ADR 0016](docs/adr/0016-terminal-driver-selection.md).

### Changed
- **Dev builds are prereleases, and `cobalt --version` now names the commit.** The version is
  `0.3.0`, and any build that doesn't set `Version` explicitly — every local and branch build —
  reports `0.3.0-alpha` and packs as `cobalt-tui.0.3.0-alpha.nupkg`. Tagged releases still pack a
  clean `0.3.0`: the release workflow passes the tag's version, which overrides the suffix.
  `--version` now keeps the source revision it used to discard (`0.3.0-alpha+fd36e1b`) — the number
  alone can't identify a build, since every build between two releases shares it, so a stale
  install was indistinguishable from a fresh one.

### Fixed
- **Comments, thread replies and mark-viewed could act on the wrong file.** Moving to a file whose
  diff is still loading left the cursor on the new file while the previous one was still on screen.
  Anything done in that window — adding a line comment, opening and replying to a thread, marking
  viewed, jumping with `]t`/`[t` — used the file under the cursor rather than the file being looked
  at, so a comment could land on a different file than the one it was written about. Everything that
  acts on the diff now keys off the diff on screen.
- **A failed review-thread load could look like a PR with no comments.** If fetching the review
  threads failed while the diff itself loaded, the error was replaced by the normal file header as
  soon as you moved to another file, and the title read `0 unresolved` — indistinguishable from a
  clean PR, on a PR that had comments. The threads are only fetched once per diff session, so this
  persisted silently for the whole review. The title now reads `comments unavailable` for the rest
  of the session.

### Performance
- **Faster PR opens.** Opening a pull request costs about three network round-trips instead of
  five: the two file blobs are fetched concurrently rather than one after the other, and the
  review threads are fetched alongside them instead of waiting their turn. Switching to a file
  that isn't cached yet is one round-trip instead of two, and the background prefetch and your
  own navigation now share a single fetch when they land on the same file rather than each
  requesting it. See [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md).
- **No more stutter on large diffs.** The diff pane used to re-syntax-highlight the whole file
  every time it redrew, so expanding a fold, searching, toggling side-by-side or landing a
  comment stalled the UI for as long as the file was big — about 64ms on a 10,000-line file,
  on every one of those keypresses. Each file's highlighting is now computed once and reused
  (~0.31ms), and only the lines that actually changed are recomposed. Marking a file viewed no
  longer rebuilds the pane at all, intra-line highlighting is capped on very long (e.g.
  minified) lines, and the per-file stats arriving in the background now refresh the chrome
  once per burst instead of once per file.
- **Snappier UI.** Cut redundant redraws and re-tokenization that made navigation feel laggy:
  the diff-review pane no longer re-tokenizes the open file once per file in the PR while the
  background stats prefetch runs (it now refreshes only the title totals and file-row stats);
  a routine status/log message repaints just the chrome labels instead of relaying out the
  whole app; and a burst of PR comment-count arrivals coalesces into a single list re-render
  instead of re-formatting every row per count.

### Added
- **Themes.** `theme = "dark" | "light" | "system"` in `config.toml` (default `dark`, the
  original look), switchable live with `:theme`. Terminal.Gui's built-in themes colour the app
  chrome + syntax highlighting; a cobalt `DiffPalette` colours the diff tints, so both recolour
  together. `system` follows the OS light/dark setting live on Windows (registry watch), falling
  back to `dark` on other platforms for now. See [ADR 0019](docs/adr/0019-hybrid-theming.md).
- **In-TUI editor for comments and replies.** Line comments, thread replies, PR replies, and
  PR-level comments are now typed in a fast in-TUI field instead of handing the terminal to
  `$EDITOR` — no suspend/resume, so no slow-editor start or driver quirks, and it's instant.
  **Enter** submits, **Esc** cancels, **Ctrl-J** (or Ctrl-Enter where the terminal delivers it)
  inserts a newline, **Ctrl-E** escalates to `$EDITOR` and returns for review. Thread-id and
  assignee prompts are one-line fields. Descriptions and tags still use `$EDITOR`. See
  [ADR 0020](docs/adr/0020-in-tui-text-input.md).

### Fixed
- **A slow or broken `az` no longer blocks sign-in.** The credential chain now tries cobalt's
  own persisted browser sign-in first and reuses an `az login` session only as a fallback —
  previously `az` was tried first, and because Azure CLI's process *timeout* is a hard failure
  that halts the chain (not a fall-through), a slow/locked-down `az` made even a successful
  `cobalt auth login` report "NOT signed in". Az's process timeout is also raised so a
  slow-but-working CLI doesn't time out.
- **Diff-review UAT polish.** `Enter` on the diff pane now opens the line's comment thread
  (it was being swallowed by the dialog's default-accept and closing the review). Searching
  the diff (`/`) uses an **inline search bar** instead of launching `$EDITOR`. The
  viewed/unviewed marker is now a clear leading `[✓]`/`[ ]` column.
- **A resolved/reactivated thread updates in the open overlay.** `x`/`u` (and a posted reply)
  in the thread overlay now re-render its body from the refreshed thread state in place —
  previously the overlay kept showing the status/comments it opened with until you closed and
  reopened it.
- **Detail/overlay windows now scroll a line at a time with visible feedback.** The
  read-only text panes (PR detail, work-item detail, and the `?`/`:messages`/key-reference
  overlays) moved an invisible caret that only scrolled the viewport once it reached the
  bottom edge, so `j`/`k`/`Ctrl-d` looked inert until then. They now scroll like a pager —
  every key advances the view immediately — and show a scroll-bar position indicator. `G`
  scrolls the last page into view (last line at the bottom).

### Added
- **Horizontal scroll and open-branch in diff review.** `h`/`l` (or `←`/`→`) scroll the focused
  pane horizontally for long lines (so those keys scroll rather than back/open inside diff
  review — close it with `q`/`Esc`); `g b` opens the PR's source branch in the browser (also
  from PR detail).
- **The diff review is now a complete review surface.** In diff review you can read a line's
  comment thread(s) with `o`/`Enter` and **reply (`c`) / resolve (`x`) / reactivate (`u`)**
  right there; **vote** on the PR with `v`; and **mark a file viewed** with `m` / **unviewed** with
  `M` (a ✓ in the tree). Navigation gained vim bracket motions (count-aware): `]c`/`[c` between **change
  hunks**, `]t`/`[t` between **comment threads**, `]v`/`[v` between **unviewed** files
  (`]`/`[` file nav moved to `]f`/`[f` to free the vim-standard `]c`). Unchanged context is
  **folded** by default (`e` expands a fold, `E` the whole file); `/`+`n`/`N` searches within
  the file; `T` filters the file list to files with **unresolved threads** (with a header
  count); and each file row shows its `+added −deleted` stat (PR total in the header) as
  diffs load in the background.
- **Branch-policy / build status and PR-level comments in PR detail.** The PR detail view
  shows the pull request's policy evaluations (build, required reviewers, comment resolution)
  with pass/fail and a blocking marker; `g c` posts a PR-level (non-line) comment.
- **View existing comments on a diff line.** In diff review, `o`/`Enter` on the diff pane
  opens the comment thread(s) anchored to the selected line (the ones flagged by the `●`
  marker) in a scrollable overlay — previously the marker showed a thread existed but its
  comments could not be read from the diff.
- **Responsive diff-review layout.** The two panes now degrade gracefully as the terminal
  shrinks: below a width threshold the changed-file list hides so the diff pane spans the
  whole row, and side-by-side automatically falls back to unified when the diff pane is too
  narrow for two columns. The file-list width scales with the terminal (clamped) and the
  layout re-applies on resize. The threshold decision is a pure, unit-tested view model.
- **Side-by-side diff toggle.** `s` switches the diff pane between the unified view and a
  two-column side-by-side view (old on the left, new on the right). Rows pair the k-th
  removed line with the k-th added line — the same rule `DiffService` uses for intra-line
  emphasis — so a modified line's before/after sit on one row, with surplus lines shown
  one-sided. The composer is pure and unit-tested; comment anchoring maps the selected
  side-by-side row back to the correct original line and side (new-side preferred, old side
  for a deletion-only row). The cursor keeps its line across a toggle.
- **Directory-tree file list in diff review.** The changed-file list is now a
  collapsible directory tree instead of a flat, left-truncated path list: files group
  under their folder, single-child directory chains compress (`src/Web/Api`), and the
  distinguishing filename always shows in full. `z` collapses/expands the folder under
  the cursor (`Enter` on a folder row also toggles); `[`/`]` and `Enter` still step
  through files, skipping folder headers. The tree projection is a pure, unit-tested
  view model (ADR 0004).

## 0.2.0 — 2026-07-07

### Added
- **Team PR tab.** A new `team` sub-tab (`Tab` order: review queue / team / mine /
  active) shows the RAW UNION of PRs where a team you belong to is a requested
  reviewer and PRs a teammate authored — deduped by PR id, newest first. It does not
  exclude your own PRs or PRs already on other tabs. Team membership is resolved once
  per session via one org-level `_apis/teams?$mine=true` call plus one members call
  per team, then cached; the reviewer half uses `searchCriteria.reviewerId={teamGuid}`
  (a team is an identity) and the author half reuses the active list filtered by
  author. Honors `:scope` (project scope restricts to teams in the context project).
  Custom/per-team views are deferred to v2. See
  [ADR 0015](docs/adr/0015-team-pr-view.md).
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
  count multiplies `Ctrl-d`/`Ctrl-u`. A bare `0` (no count started) is ignored, so it is
  never mistaken for a count; it only extends an existing one (`10j`). Implemented as a
  pure `KeymapRouter` change (`KeyResult.Count`) threaded through the shell and dialogs.
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
- **`l` opens the selected item and `h` goes back/closes** (vim "move into"/"move out"),
  alongside `Enter`/`o` and `q`. They were previously bound to unimplemented
  `MoveRight`/`MoveLeft`, so they only reported "not available".
- **`Ctrl-d`/`Ctrl-u` are a true half page everywhere.** In lists they previously did a
  full page (the `ListView` primitive); they now move half the viewport, matching the
  detail dialogs and vim, via the shared `VimScroll` seam.
- **Narrower view-model error handling.** View-model loads/mutations now catch only
  the expected Azure DevOps failure set (API/HTTP/JSON/auth/IO) and surface it in the
  message bar; an unexpected exception propagates to the crash boundary instead of
  being masked as an "error" string (CodeQL `cs/catch-of-all-exceptions`).

### Fixed
- **Dropped input and broken editor handoff under terminal multiplexers.** Inside zellij
  (and tmux), fast repeated motions lost keystrokes (`j`×6 landed on row 4) and `$EDITOR`
  opened a dead, un-quittable buffer. Root cause: Terminal.Gui's default Win32-console
  `windows` driver is unreliable through a multiplexer's pseudo-terminal. cobalt now
  **auto-detects zellij/tmux** (`ZELLIJ`/`TMUX`) and switches to the `dotnet` driver; a
  `COBALT_DRIVER` environment override (`windows`/`dotnet`/`ansi`) covers any other
  multiplexer or forces a specific driver. The default on a bare console is unchanged. See
  [ADR 0016](docs/adr/0016-terminal-driver-selection.md).
- **Cross-project work-item drill-in under org scope.** With `:scope org` the list spans
  every project, but pressing `s`/edit/comment/open on an item from another project used
  to query the context project (wrong state list, or a 404). The detail/mutation path now
  threads the item's own `System.TeamProject` through the `GET`/`PATCH`/states/comments
  routes (states are per-project process metadata, so they must). The work-item list also
  shows a **project column** when its rows span more than one project, so cross-project
  items are distinguishable. See
  [ADR 0011](docs/adr/0011-cross-project-org-pr-scope.md).
- **`:project` on the PR list is now an exact match.** It previously used a substring
  match, so `:project Web` also kept a `WebApps` PR; it now matches the project name
  exactly (case-insensitive), aligning with the work-item side's WIQL equality. The
  repository filter stays a substring match.
- **Work-item list load-race.** A slow work-item load (e.g. `:done show`) followed by a
  fast one (`:done hide`) could let the slow result land last and overwrite the fast one;
  a monotonic load-sequence guard now lets only the newest load commit its rows.
- **The `?`/`:messages` overlays actually vim-scroll.** They route movement through the
  shared `KeymapRouter`/`VimScroll` seam, so `j/k`, `gg`/`G`, and `Ctrl-d`/`Ctrl-u` (with
  counts) scroll the pane; previously the read-only `TextView` swallowed those keys.
- **Dialog `?` help lists only keys the dialog handles.** A modal's key reference no
  longer advertises dead global keys (`r`, `/`, `yy`, `gt/gT`, `Tab`) or verbs from other
  scopes. Relatedly, the diff-review scope no longer binds `x`/`u`/`v` (resolve/reactivate/
  vote) — those belong to PR detail and the diff dialog implemented none of them.
- **`Esc` clears a pending count before closing a dialog.** With a count part-entered
  (`5`…), `Esc` now cancels the count instead of closing the dialog; it closes only when
  nothing is pending, mirroring the shell.
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
