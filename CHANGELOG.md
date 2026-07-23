# Changelog

## 0.3.2 — 2026-07-24

No user-facing change. This release carries dependency updates and the internal foundations
for the list + preview workspace ([ADR 0024](docs/adr/0024-list-preview-workspace.md)); the
workspace itself is not enabled yet.

### Internal
- **Detail text composition is extracted into pure formatters.** `PrDetailFormatter` and
  `WorkItemDetailFormatter` render a detail view-model at a given width and tier
  (`Summary`/`Full`); the PR and work-item dialogs delegate to them instead of composing their
  text inline. Behaviour is unchanged and pinned byte-for-byte by snapshot tests — the point is
  that the coming preview pane renders from the same function rather than growing a second,
  drifting implementation.
- **A shell-wide two-pane layout calculator.** `WorkspaceLayout.Compute(width)` decides whether
  a preview pane is shown and how the width splits, as pure unit-tested thresholds. The diff
  dialog's `ResponsiveLayout` is untouched — the two surfaces share the shape, not the table.
- **The [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md) load invariants are now
  types rather than conventions.** `SingleFlightCache<K,V>` owns a single cancellation token
  source and a monotonic supersede stamp, so a superseded fetch can neither publish its stale
  result nor leak its fault to the crash log; `Published<T>` makes a torn read of a paired state
  impossible by construction. Both generalize patterns that were previously re-copied by hand —
  the source of four earlier wrong-file bugs.
- **Workspace pane focus is an explicit view-model.** `WorkspaceViewModel` owns the focused pane
  and the key-routing decision, so routing is unit-tested headlessly instead of inferred from
  Terminal.Gui focus. `Tab` is bound to pane cycling in the two list scopes but falls back to
  today's tab behaviour while the preview is hidden — which it always is until the workspace
  ships, hence no visible change here.

### Dependencies
- `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.TimeProvider.Testing`
  10.7.0 → 10.8.0, `Microsoft.NET.Test.Sdk` 18.7.0 → 18.8.1, `Microsoft.SourceLink.GitHub`
  10.0.300 → 10.0.301; `actions/setup-dotnet` v5 → v6 in CI.

## 0.3.1 — 2026-07-23

### Fixed
- **A diff-review render could pair one file's diff with another's path.** The render path read
  `CurrentDiff` and `CurrentDiffPath` as two separate reads of the view model's diff state, so an
  overlapping file select could tear the pair — this file's lines described under the next file's
  name (and its comment threads). Every consumer that needs both now takes a single
  `CurrentDiffSnapshot` read, which returns the diff and its path atomically. See
  [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md).

### Internal
- **UI-thread marshalling goes through a one-member `IUiPost` seam.** Background continuations
  that touch widgets now post through `IUiPost.Post` instead of passing Terminal.Gui's
  `IApplication` around solely for its `Invoke`. `Post` always queues onto the main loop (never
  runs inline), which the coalescing gates in diff-review stats-refresh and PR-list count-badges
  depend on. Enforced by the `ViewModelPurityTests` reflection backstop. See
  [ADR 0004](docs/adr/0004-terminal-gui-v2-with-viewmodels.md).
- **The timeout-vs-cancel scaffold is deduped into `VmGuard.RunAsync`**, so the several
  view-model mutation paths that distinguish an operation timeout from a caller cancellation share
  one implementation instead of repeating it.
- **The diff-pane composition is extracted into a pure `DiffPaneComposer`.** All of
  `DiffReviewDialog.Render`'s mode/fold/search branching now lives in a unit-testable, Terminal.Gui-free
  composer; the view only decides fold reuse and stores what it returns (ADR 0004).

## 0.3.0 — 2026-07-22

### Added
- **Keybinding remap config.** A `[keys.<scope>]` table in `config.toml` overrides or extends
  the default bindings, scoped to a lowercased `KeyScope` (`global`, `workitemlist`,
  `workitemdetail`, `pullrequestlist`, `pullrequestdetail`, `diffreview`, `threadview`). Each
  entry is `command-name = "token sequence"` or an array for multiple sequences (`move-down =
  ["n", "g j"]`); a config entry **replaces** that command's default bindings in its scope
  (not additive — repeat the default if you want it kept alongside a new one), and
  `command-name = ""` unbinds it. An unknown scope or command, a sequence that conflicts with
  another command's binding in the same scope, or binding a reserved sequence (`Esc`, or one
  starting with a bare digit) fails startup with the offending scope/command/sequence named.
  The keybar and `?` help render from the live binding table, so a remap needs no other
  change — and it reaches the modal dialogs too. See
  [ADR 0023](docs/adr/0023-keybinding-remap-config.md).
- **`:` command palette completion.** `Tab` / `Shift-Tab` in the `:` palette complete and
  cycle command names, and for `:context`/`:project`/`:theme`, their argument names too (known
  context names, project names drawn from the loaded lists, and the theme names).
- **`--config <path>`.** Launch (or `auth login`/`auth status`) against a specific config.toml
  instead of the default location — useful for trying a `[keys]` remap or a different context
  set without editing your real config. A missing file fails with the usual clear config error.
- **Config typos fail loud.** An unknown root-level key or table (`[key.global]` for
  `[keys.global]`), an unknown key inside a `[contexts.*]` section, a repeated table header,
  or a keybinding token no keypress can produce (`"5j"`, an escaped control character) now
  all fail at startup naming the offender — previously each was silently ignored.
- **`:log` operations view.** `:log` opens a scrollable dialog listing recent Azure DevOps
  requests — operation name, route shape, duration, and outcome — for transparency into what
  cobalt is doing. The route shape masks numeric IDs and GUID path segments to `{id}` and
  trims the query to `api-version`; headers, tokens, and any other query text never reach the
  log, by construction (the only way to record an operation pipes it through the masking
  function first).

### Changed
- **Helpful empty states.** The PR and work-item lists now explain an empty result instead of
  just showing nothing: the Team PR tab (the default, and inherently org-setup-dependent)
  reads as empty by design, not broken, and points at `]`/`:scope org`; a list narrowed to
  zero by an active filter names the filter and how to clear it.
- **Colour degradation.** cobalt now detects the terminal's colour support — truecolor,
  16-colour ANSI, or monochrome — from `NO_COLOR`, `COLORTERM`, `TERM`, `TERM_PROGRAM`, and
  `WT_SESSION`, and degrades the chrome and diff palette to match instead of assuming
  truecolor. A `COBALT_COLOR=none|16|true|full` override forces a tier (an unrecognised value
  fails at startup rather than being ignored). In monochrome, diff rows carry no colour tint —
  the `+`/`-` gutter sign and attribute emphasis carry the meaning instead. See
  [ADR 0019](docs/adr/0019-hybrid-theming.md).

### Changed
- **The work-item list is capped at the first 200 assigned items**, matching the existing
  pull-request-list cap: a heavy assignee no longer pulls an unbounded id set (and that many
  batch reads). See "Known limitations" in the README.
- **PR tabs, and the work-item/PR sections themselves, now keep what they last loaded when you
  switch away and back.** Revisiting a tab or section paints its last-known rows instantly and
  refreshes them in the background, instead of blanking the pane and re-fetching every time —
  rows can be a few seconds stale until the refresh lands; `r` still forces a fresh fetch, and a
  `:scope`/context change still clears the cache. See
  [ADR 0008](docs/adr/0008-client-side-diff-and-line-comments.md).

### Performance
- **Diff review does less work per keystroke.** Folding, searching, and toggling
  unified/side-by-side no longer rebuild the file tree — only actions that change what's shown
  (resolve, mark-viewed, filter, stats refresh) still do. The unresolved-comment marker is now
  precomputed once per thread refresh instead of scanning every file's threads on each render,
  and the intra-line word diff skips its expensive comparison on line pairs whose lengths are
  too mismatched for the result to be useful. Vim movement now redraws only the moved list
  instead of repainting the whole app on every keystroke (both-driver UAT passed 2026-07-22 —
  see [ADR 0016](docs/adr/0016-terminal-driver-selection.md)). Per-keystroke
  input handling (the key router and tokenizer) no longer allocates on the hot path. These are
  structural fixes verified by allocation/call-count tests, not measured against a live org.
- **Fewer round-trips on work items.** Opening the work-item detail now fetches its comments and
  allowed states concurrently instead of one after another; work-item-type states are cached
  per project instead of re-fetched on every open; the cold-start identity check makes one
  `connectionData` call instead of two; JSON responses are stream-parsed instead of buffered to
  an intermediate string first.

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
- **Vim `j`/`k` no longer needs two presses per move on a bare terminal.** Terminal.Gui
  2.4.17's auto-detect selects its new `ansi` driver, whose input path drops every other
  keypress. Cobalt now pins the platform default explicitly (`windows` on Windows, `dotnet`
  elsewhere) instead of falling through to auto-detect; `COBALT_DRIVER` still overrides
  everything, including `=ansi`. See
  [ADR 0016](docs/adr/0016-terminal-driver-selection.md).
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
- **Context keybar, showcmd, and a real tab strip** (the first slice of the
  lazygit-inspired redesign). The bottom row now always shows the keys available in
  the current context, generated from the live binding table so it can never drift
  from behaviour; the status row shows an armed count or pending chord
  (vim's showcmd) and clears it on `Esc`; the section tabs carry their `g1`/`g2`
  jump chords; and the PR sub-tabs render as a visible tab row
  (` [team 7] │ mine │ active`) — cycled with `[`/`]`
  (lazygit's panel-tab keys) as well as `Tab`. See
  [ADR 0021](docs/adr/0021-lazygit-inspired-shell-chrome.md).
- **PR rows show the author** — a new author column between age and repo, sized to
  the longest name in the result set (capped).
### Changed
- **PR sub-tabs are now team / mine / active (team default).** The personal
  "review queue" tab left the cycle: it only ever listed PRs where *you personally*
  are a requested reviewer and haven't voted, which is permanently empty in orgs
  that request reviews via teams — the Team tab is the real queue there. (The
  filter itself remains in the client for a future config-enabled view.)
- Section tab labels no longer carry the `g1`/`g2` jump chords (UAT feedback) —
  the chords remain discoverable via `?` help.
- Version bumped to 0.3.0.
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
