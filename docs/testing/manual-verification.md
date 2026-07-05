# Cobalt — Manual Verification (UAT) Checklist

## 1. Purpose & how to use

Cobalt's automated suite (unit tests + PTY smokes) runs entirely against faked
HTTP — the app has never been exercised against a real Azure DevOps
organization. This document is the human validation pass: work through it top
to bottom against your real ADO org, ticking each item. For every item, mark
the **Result** line `Pass` or `Fail` and add notes. On any failure, capture
(a) the exact on-screen text (the bottom message bar and/or stderr), (b) the
`:messages` log (open the palette with `:`, type `messages`, Enter — each line
is `HH:mm:ss I|E <text>`), and (c) the work item / PR id involved. Every
"Expect:" below quotes real strings from the code at the commit under test —
if the wording differs materially, that is itself a finding.

> Scope note: the README's "Known limitations (v1)" section is partly stale.
> Editor suspend/resume, intra-line word highlighting, syntax highlighting,
> and rename-aware diffs **have since been implemented** (ADR 0009, ADR 0010,
> commits `ceba2f9`, `c77ccaf`). Trust this document and the ADRs, not the
> README, for what to expect.

## 2. Environment capture

Fill in before starting:

| Field | Value |
|---|---|
| Date / tester | |
| OS + version | |
| Terminal emulator | |
| `$TERM` | |
| Truecolor terminal? (`$COLORTERM`) | |
| .NET SDK version (`dotnet --version`, needs 10.0.100+) | |
| `$EDITOR` / `$VISUAL` | |
| ADO organization URL | |
| ADO project | |
| Auth method used (az CLI reuse / `cobalt auth login` browser) | |
| az CLI tenant same as ADO org tenant? | |
| cobalt version (`cobalt --version`) / commit | 0.1.0 / `0e103df` |

## 3. Prerequisites & test fixtures to create in ADO

Set these up in your org/project before starting so every path is reachable.
The "Used by" column names the checklist items that need each fixture.

| # | Fixture | Used by |
|---|---|---|
| FX1 | At least one work item **assigned to you** in a non-terminal state (not Closed/Done/Removed/Completed) — the WIQL query filters on `@Me` and excludes exactly those four states | E1–E4, D10–D12 |
| FX2 | A work item you can safely **transition state** on (and transition back) | E6 |
| FX3 | A work item with a **rich-HTML description** — e.g. paste a table from the web editor, or use inline colors/styles (a `<table>` or `style=` attribute triggers the flag) | E5b |
| FX4 | A work item with a **plain-text / simple description** (paragraphs, bold, lists only) | E5a |
| FX5 | A work item with **tags, priority, and story points** set (display check) | E4 |
| FX6 | A PR where **you are a reviewer and have not voted** (review queue tab) | G2, G8 |
| FX7 | A PR **you authored** (mine tab) | G3 |
| FX8 | Any **active PR** in the project (active tab) — ideally ≥1 draft PR too | G4 |
| FX9 | A **sacrificial PR safe to complete** (with pass-able policies) and one **safe to abandon** | G13, G14 |
| FX10 | A PR whose latest iteration contains: (a) an **edited code file** in C#, JS/TS, JSON, or Python (for syntax colors); (b) a **rename-with-edit** (git `mv` + change a line, commit, push); (c) if possible a **binary file** (e.g. a .png) and/or a **>50,000-line file**; (d) a file containing **CJK or emoji characters** on changed lines (to observe the documented wide-char limitation, NOT to log as a bug) | H1–H12 |
| FX11 | A PR with an **existing comment thread** (create one from the web UI if needed) — note its thread id | G10–G12, H9 |
| FX12 | A PR with **linked work items** (link FX1 to FX7 or FX8) | G7 |
| FX13 | A **second context** in your config — a second project (or org) you have access to; a config with a deliberately **unreachable org** for the broken-auth-status test | B, C4, D9 |

Also required on the machine: the `az` CLI (for the reuse path), a browser
(for interactive sign-in and `gx`), and a real full-screen `$EDITOR`
(`nvim`/`vim`/`nano`) on `$PATH`.

## 4. Setup steps

1. **Install .NET 10 SDK** (the repo pins `10.0.100`, rollForward
   latestFeature). Verify: `dotnet --version`.
2. **Install cobalt** (command name is `cobalt`, package id `cobalt-tui`):
   ```sh
   dotnet tool install -g cobalt-tui                      # from nuget.org, or:
   dotnet tool install -g cobalt-tui --add-source ./nupkg # from the repo's local package
   ```
   Alternatively run from source: `dotnet run --project src/Cobalt -- --help`.
3. **Create the config** at `~/.config/cobalt/config.toml`
   (`$XDG_CONFIG_HOME/cobalt/config.toml` if set; `%APPDATA%\cobalt\config.toml`
   on Windows) with **two contexts** (FX13):
   ```toml
   default_context = "work"

   [contexts.work]
   organization = "https://dev.azure.com/YOUR_ORG"   # or a bare "YOUR_ORG"
   project = "YOUR_PROJECT"

   [contexts.alt]
   organization = "https://dev.azure.com/YOUR_ORG"
   project = "YOUR_OTHER_PROJECT"
   ```
4. **Sign in — two paths, test both** (section C):
   - az CLI reuse: `az login` (same tenant as the ADO org), then no cobalt
     login step is needed at all.
   - Browser: `cobalt auth login` (prints `Opening a browser for Entra ID
     sign-in…`, persists an auth record to
     `~/.config/cobalt/auth-record.json`).
5. **Verify**: `cobalt auth status`, then launch with `cobalt`.
6. Ensure `$EDITOR` (or `$VISUAL`, which wins) points at your editor,
   e.g. `export EDITOR=nvim`. If both are unset, cobalt falls back to `vi`.

---

## 5. Checklist

Record each as: `Result: Pass / Fail — notes:`

### A. CLI basics & exit codes

Check exit codes with `echo $?` immediately after each command.

- [ ] **A1 — version.** Run `cobalt --version` (and `cobalt -v`).
  Expect: prints `0.1.0` (no git-hash suffix), exit `0`.
  Result: ______
- [ ] **A2 — help.** Run `cobalt --help` (and `-h`).
  Expect: usage block starting `cobalt — a vim-flavored terminal UI for Azure
  DevOps` listing `cobalt [--context <name>]`, `cobalt auth login`,
  `cobalt auth status`, `--version`, `--help`. Exit `0`.
  Result: ______
- [ ] **A3 — unknown argument.** Run `cobalt --bogus`.
  Expect: stderr `cobalt: unknown argument '--bogus' (try --help)`, exit `2`.
  Result: ______
- [ ] **A4 — `--context` without a value.** Run `cobalt --context`.
  Expect: stderr `cobalt: --context requires a value (a context name from your
  config).`, exit `2`. Also try `cobalt --context --context` (value starting
  with `-` is rejected the same way).
  Result: ______
- [ ] **A5 — `--context` twice.** Run `cobalt --context work --context alt`.
  Expect: stderr `cobalt: --context was given more than once.`, exit `2`.
  Result: ______
- [ ] **A6 — bad auth verb.** Run `cobalt auth` and `cobalt auth frobnicate`.
  Expect: stderr `cobalt: usage: cobalt auth <login|status>`, exit `2`.
  Result: ______
- [ ] **A7 — unknown context name.** Run `cobalt --context nope`.
  Expect: fails fast **before** the TUI starts: stderr
  `cobalt: context 'nope' not found; available: alt, work` (names sorted),
  exit `2`.
  Result: ______

### B. Config validation

For each case, temporarily replace `config.toml`, run `cobalt`, check stderr
and exit code `2`, then restore. (Keep a backup copy.)

- [ ] **B1 — missing file.** Move the config aside; run `cobalt`.
  Expect: `cobalt: no config file at <full path>` followed by a template
  (`create one like:` … `default_context = "work"` …). Exit `2`.
  Result: ______
- [ ] **B2 — invalid TOML.** Put `default_context = ` (no value) in the file.
  Expect: `cobalt: config is not valid TOML: <Tomlyn parser message>`.
  Result: ______
- [ ] **B3 — empty file / no contexts.** (a) Empty file → `cobalt: config file
  is empty`. (b) Only `default_context = "work"` → `cobalt: config defines no
  [contexts.*] sections`.
  Result: ______
- [ ] **B4 — missing keys.** A context without `organization` →
  `cobalt: [contexts.work] is missing 'organization'`; without `project` →
  `… is missing 'project'`.
  Result: ______
- [ ] **B5 — partial-URL org.** `organization = "myorg/proj"` →
  `cobalt: [contexts.work] organization 'myorg/proj' looks like a partial URL;
  use either a bare org name or a full https:// URL`.
  Result: ______
- [ ] **B6 — non-https org.** `organization = "http://dev.azure.com/x"` →
  `cobalt: [contexts.work] organization must be an https URL or a bare org
  name, got 'http://dev.azure.com/x'`.
  Result: ______
- [ ] **B7 — unknown default_context.** `default_context = "ghost"` with no
  `[contexts.ghost]` → `cobalt: default_context 'ghost' has no matching
  [contexts.ghost] section`.
  Result: ______
- [ ] **B8 — multiple contexts, no default.** Remove `default_context` (keep 2
  contexts); run `cobalt` with no flag →
  `cobalt: multiple contexts defined but no default_context set; pick one of:
  alt, work`. Then confirm `cobalt --context work` still launches.
  Result: ______
- [ ] **B9 — bare org name.** `organization = "YOUR_ORG"` (no URL).
  Expect: works identically to the full URL (normalized to
  `https://dev.azure.com/YOUR_ORG`) — `cobalt auth status` shows the full URL.
  Result: ______

### C. Auth

- [ ] **C1 — az CLI reuse.** With a valid `az login` session in the org's
  tenant and **no** `auth-record.json` / prior cobalt login, run
  `cobalt auth status`.
  Expect: **no browser opens**; each org line lists its contexts as
  `* work         https://dev.azure.com/ORG [Project]` (`*` marks the default
  context, others get a space) followed by an indented
  `    signed in as <Display Name> (<guid>)`. Exit `0`.
  Result: ______
- [ ] **C2 — interactive browser login.** `az logout` (or use a machine
  without az), then run `cobalt auth login`.
  Expect: prints `Opening a browser for Entra ID sign-in…`, a browser
  sign-in completes, then `Signed in as <username>.` followed by the same
  status listing as C1. Verify `~/.config/cobalt/auth-record.json` now exists
  with `-rw-------` permissions (non-Windows).
  Result: ______
- [ ] **C3 — silent reuse after login.** Run `cobalt auth status` again after
  C2. Expect: no browser (the persisted MSAL cache + auth record make it
  silent), same signed-in output, exit `0`.
  Result: ______
- [ ] **C4 — deliberately-broken context.** Add a context pointing at an org
  you cannot access (e.g. `organization = "somebodyelsesorg"`), run
  `cobalt auth status`.
  Expect: the good org still reports `signed in as …`; the bad org's line
  reports `    NOT signed in — <first line of the error>` (no stack trace),
  and the exit code is `1`. Contexts sharing one org are grouped — identity is
  asked once per org.
  Result: ______
- [ ] **C5 — not signed in at all.** `az logout` and delete
  `~/.config/cobalt/auth-record.json`, then `cobalt auth status`.
  Expect: `NOT signed in — …` for every org, exit `1`, and **no browser is
  opened** (the TUI/status paths never prompt; only `auth login` may).
  Result: ______
- [ ] **C6 — TUI without sign-in.** Still signed out, run `cobalt`.
  Expect: the TUI launches (startup never blocks on auth); the work-item list
  header shows ` error: <message>` and the message bar shows
  `not signed in — <reason> (run: cobalt auth login)`. No crash, no stack
  trace. Quit with `:q`.
  Result: ______
- [ ] **C7 — wrong-tenant token (if reproducible).** If your az CLI is logged
  into a different tenant than the org, ADO answers 203 with a sign-in page;
  cobalt converts that to
  `Azure DevOps did not accept the access token (are you signed in to the
  right tenant?)`. Expect that text in `auth status` / list errors — then
  `az login --tenant <correct>` fixes it. (Skip if not applicable.)
  Result: ______

### D. Shell & vim layer

Sign in properly, run `cobalt`. The shell is: tab strip (top), content,
status line (` ctx:work  <Your Name>` once identity resolves), message bar
(bottom).

- [ ] **D1 — startup.** Expect: work-items section active — tab strip reads
  ` [1:Work Items]  2:Pull Requests `; shortly after, the message bar shows
  `signed in as <Display Name>` and the status line gains your name.
  Result: ______
- [ ] **D2 — section keys.** Press `2` → tab strip flips to
  ` 1:Work Items  [2:Pull Requests]` and the PR list loads. Press `1` → back
  to work items.
  Result: ______
- [ ] **D3 — Tab semantics.** In the work-items section press `Tab` → switches
  to the PR section. In the **PR section** `Tab`/`Shift-Tab` do NOT return to
  work items — they cycle the PR sub-tabs (review queue → mine → active);
  only `1` returns. Verify both behaviors.
  Result: ______
- [ ] **D4 — list movement.** In the work-item list: `j`/`k` move the
  selection down/up; arrow keys also move; `gg` jumps to top, `G` to bottom;
  `Ctrl-d`/`Ctrl-u` half-page.
  *Tester note:* `j/k/gg/G/Ctrl-d/Ctrl-u` are advertised in the README and the
  `?` overlay, but `CobaltShell.Dispatch` has no movement cases and the main
  list views (unlike the diff dialog) don't null the ListView's type-ahead
  navigator — watch closely whether the vim movement keys actually move the
  selection or are swallowed. Record exactly which keys work; failures here
  are real findings.
  Result: ______
- [ ] **D5 — filter.** In the work-item list press `/` → a `/`-prefixed input
  appears on the bottom line; typing filters rows live by title, state, type,
  or id (case-insensitive); `Enter` or `Esc` closes the input keeping/clearing
  focus to the list. Clear the filter text to restore all rows.
  Result: ______
- [ ] **D6 — palette basics.** Press `:` → a `:` prompt replaces the message
  bar. `Esc` cancels. `:` then `garbage` + Enter → message bar shows
  `unknown command: garbage`. Empty input + Enter → nothing.
  Result: ______
- [ ] **D7 — `:help` and `?`.** Both open a modal titled `keys — q to close`
  listing bindings with descriptions (e.g. `j        move down`,
  `:        command palette (:q quit, :ctx NAME)`); aliases collapse (Enter/o
  shows once). `q`, `Esc`, or `Enter` closes it.
  Result: ______
- [ ] **D8 — `:messages`.** Open the palette, type `messages`, Enter.
  Expect: modal titled `messages — q to close` with one line per event:
  `HH:mm:ss I signed in as …` / `HH:mm:ss E …` (I=info, E=error), capped at
  200 entries.
  Result: ______
- [ ] **D9 — context switching.** `:ctx` (no arg) → message bar
  `contexts: alt, work — switch with :ctx NAME`. `:ctx nope` →
  `unknown context 'nope' (available: alt, work)`. `:ctx alt` → status line
  changes to ` ctx:alt` and message `switched to context 'alt'`.
  **Known v1 gap (do not log as a bug):** the in-app switch updates the
  chrome only — the data connection is *not* rebuilt (see the
  `Real reconnection lands with the data screens (M3)` comment in
  `CobaltShell`), so lists keep showing the launch context's data. Verify
  instead that `cobalt --context alt` from the CLI shows the other project's
  items for real.
  Result: ______
- [ ] **D10 — refresh.** Change a work item's title in the ADO web UI, press
  `r` in the list. Expect: header flashes ` loading work items…` then the row
  updates.
  Result: ______
- [ ] **D11 — `yy` yank.** Select a work item, press `y` `y`.
  Expect: `yanked https://dev.azure.com/ORG/PROJECT/_workitems/edit/<id>` and
  the URL is on the system clipboard; on a clipboard-less terminal instead
  `clipboard unavailable — <url>` (the URL is still shown — that's the
  fallback). In the PR section the URL shape is
  `…/PROJECT/_git/<repo>/pullrequest/<id>`.
  Result: ______
- [ ] **D12 — `gx` open in browser.** Select an item, press `g` `x`.
  Expect: default browser opens that URL; message `opened <url>`. On failure:
  `could not open browser: <error>`.
  Result: ______
- [ ] **D13 — empty-selection yank/open.** With an empty list (e.g. filter to
  nothing is not enough — needs no rows; use a context with no items or the
  error state), `yy` → `nothing to yank — select a work item or PR first`,
  `gx` → `nothing to open — select a work item or PR first`.
  Result: ______
- [ ] **D14 — `q` at top level.** Press `q` in the main shell.
  Expect: does NOT quit; message `nothing to close — quit with :q`. Then `:q`
  (or `:quit`) actually exits with code `0` and your shell prompt/terminal
  state is restored cleanly.
  Result: ______
- [ ] **D15 — pending-key handling.** Press `g` then `Esc` then `j`.
  Expect: `Esc` clears the pending `g` (no `gg` fires) and `j` moves normally.
  Also `g` followed by an unbound key (e.g. `z`) just resets — no crash.
  Result: ______

### E. Work items

- [ ] **E1 — list contents (FX1).** Section 1 header:
  ` my work items (<count>)`. Rows show columns in order: right-aligned 6-char
  id, type (8), state (10), title (40), last segment of the iteration path
  (14), changed date `yyyy-MM-dd`. Long values truncate with a trailing `…`.
  Cross-check the count and ordering (most recently changed first) against an
  ADO query `Assigned To = @Me AND State NOT IN (Closed, Done, Removed,
  Completed)`.
  Result: ______
- [ ] **E2 — terminal states excluded.** Close/complete one of your items in
  the web UI, press `r`. Expect: it disappears from the list. (Note: only the
  four states `Closed`, `Done`, `Removed`, `Completed` are excluded — a
  process with other terminal-state names, e.g. `Resolved`, will still show;
  that's per the WIQL in `WorkItemsApi`, not a bug.)
  Result: ______
- [ ] **E3 — open detail.** `Enter` (or `o`) on an item. Expect a modal titled
  `work item #<id> — q close · s state · c comment · e edit · a assign · t
  tags` showing `<Type> #<id>   [<State>]`, `Title:`, `Assigned:`
  (`(unassigned)` when empty), `Iteration:`, `Tags:`, `Priority: <n or ->   Points: <n or ->`,
  a `── Description ──` section, and `── Comments (N) ──` with
  `  <author> (yyyy-MM-dd): <text>` lines. `q`/`Esc` closes.
  Result: ______
- [ ] **E4 — field display (FX5).** Open the item with tags/priority/points.
  Expect tags comma-joined, priority and points shown as numbers.
  **Known v1 gap:** priority, story points, and title have no edit keybinding
  in the detail dialog (the view-model supports them; the UI wires only
  s/c/e/a/t) — display-only is expected.
  Result: ______
- [ ] **E5 — description rendering.**
  (a) FX4 plain item: description renders as readable Markdown (headings,
  bold, lists), header exactly `── Description ──`; an empty description shows
  `(empty)`.
  (b) FX3 rich-HTML item: header carries the lossiness warning:
  `── Description ──  ⚠ rich HTML: editing may drop formatting`.
  Result: ______
- [ ] **E6 — state transition (FX2).** Press `s`.
  Expect: a `change state` dialog whose buttons are the **process-template
  states for that work-item type** (from `workitemtypes/{type}/states` — e.g.
  New/Active/Resolved/Closed for Agile Bug), not a hardcoded list. Pick one →
  message bar `state → <State>`, the `[State]` in the detail header updates,
  and after closing the list reflects it. Verify in the web UI. Transition
  back. An invalid transition (if your process restricts any) must surface
  `failed: <ADO message>`, not crash.
  Result: ______
- [ ] **E7 — assign.** Press `a`.
  Expect: your `$EDITOR` opens on a temp `.txt` file (empty) — type a user's
  **unique name (email UPN)**, save, quit. Message bar shows the prompt title
  `assign to (unique name)` on success (yes, the success message is the prompt
  text) or `failed: <ADO message>` (e.g. for an unknown identity —
  TF-something). `Assigned:` updates. Reassign to yourself afterwards.
  Result: ______
- [ ] **E8 — tags.** Press `t`.
  Expect: `$EDITOR` opens pre-filled with the current tags joined `; `.
  Edit (semicolon-separated), save, quit → success message
  `tags (semicolon separated)`; the `Tags:` line and the web UI update.
  Result: ______
- [ ] **E9 — comment round-trip.** Press `c`.
  Expect: `$EDITOR` on an empty `.md` file. Write Markdown (e.g.
  `**bold** and a list`), save, quit → `comment added`, the comment appears at
  once in `── Comments (N) ──` (count +1) with you as author. Check the web
  UI renders it as formatted HTML (cobalt converts Markdown→HTML on save and
  HTML→Markdown on read).
  Result: ______
- [ ] **E10 — edit description in `$EDITOR` (suspend/resume).** Press `e` on
  FX4. Expect: **the TUI fully hands over the terminal** — nvim/vim takes the
  whole screen with the description as Markdown; while inside the editor,
  keys (including `q`) go to the editor only. `:wq` → the TUI redraws cleanly
  (no torn frames, no leftover editor rows), message `description saved`, and
  the new text shows in the detail and the web UI.
  Result: ______
- [ ] **E11 — lossiness warning honored (FX3).** Open the rich-HTML item,
  confirm the `⚠ rich HTML: editing may drop formatting` flag from E5b, press
  `e`, make a trivial edit, save. Expect `description saved` — then verify in
  the web UI what formatting was dropped (tables/styles degrade; this
  *degradation* is the documented lossy behavior, not a bug — the warning
  before editing is the contract).
  Result: ______
- [ ] **E12 — editor cancel paths.** Press `e`, then (a) quit without changes
  (`:q`) — expect **no** save, no message; (b) quit with a non-zero exit
  (`:cq` in vim) after editing — expect the edit is **discarded** (non-zero
  exit ⇒ discard). Same contract for `c`/`a`/`t`.
  Result: ______
- [ ] **E13 — list refresh after detail.** Close the detail after E6/E8.
  Expect the list reloads automatically (edits visible without pressing `r`).
  Result: ______
- [ ] **E14 — detail error surface.** Force an error (e.g. sign out az
  mid-session, or open a deleted work item id — do E14 last): detail body
  shows `error: <message>` (or the list shows ` error: …`); never a stack
  trace.
  Result: ______

### F. Editor round-trip specifics

- [ ] **F1 — `$VISUAL` precedence.** Set `VISUAL=nvim` and `EDITOR=nano`;
  press `c` on a work item. Expect nvim (VISUAL wins). Unset `VISUAL` →
  `EDITOR` is used. Unset both → `vi`.
  Result: ______
- [ ] **F2 — editor with arguments.** `export EDITOR="code --wait"` (or any
  command with args/quotes). Expect the argument split honors quotes and the
  round-trip still works.
  Result: ______
- [ ] **F3 — missing editor surfaces cleanly.** `export EDITOR=doesnotexist`,
  press `c`. Expect the app stays alive and the message bar shows
  `editor failed: could not start editor 'doesnotexist' — check
  $VISUAL/$EDITOR`.
  Result: ______
- [ ] **F4 — editor from PR context.** Repeat a round-trip from PR detail
  (`c` reply, G10) and from diff review (`c`, H8). Expect identical
  suspend/resume behavior and the same `editor failed: …` on failure.
  Result: ______
- [ ] **F5 — stray keys during editing.** While the editor is open, type `q`,
  `:`, `1`, `2` as text. Expect none of them leak to the TUI (the input loop
  is parked); on exit the shell is exactly where you left it.
  Result: ______
- [ ] **F6 — temp file hygiene.** After a few edits, check `$TMPDIR`/`/tmp`
  for leftover `cobalt-<guid>.md|.txt` files. Expect none (best-effort
  delete).
  Result: ______

### G. Pull requests

- [ ] **G1 — three tabs.** Press `2`. Header cycles with `Tab` through
  ` pull requests · review queue (N)   [Tab] switch` → ` … · mine (N) …` →
  ` … · active (N) …` and back; `Shift-Tab` cycles in reverse. While loading:
  ` pull requests · <tab> · loading…`; on failure:
  ` pull requests · <tab> · error: <message>`.
  Result: ______
- [ ] **G2 — review queue = awaiting MY vote (FX6).** The review-queue tab
  must list only active PRs where you are a reviewer **and your vote is
  currently "no vote"** (the pending filter is client-side). Vote on one
  (G8) and refresh (`r`): it must drop out of this tab.
  Result: ______
- [ ] **G3 — mine (FX7).** Lists only active PRs you created.
  Result: ______
- [ ] **G4 — active (FX8).** Lists all active PRs in the project (top 100).
  Row format: `!<id>  <repo(12)>  <votes>  [draft] <title(50)>` where votes
  are `✗ rej` (any reject) / `⧗ wait` (any waiting-for-author) /
  `✓ <n>` (n approvals) / `·` (none), and `[draft]` marks draft PRs.
  Result: ______
- [ ] **G5 — repo filter.** **Known v1 gap (do not log as bug):** the
  repository filter exists only in the view-model (`RepositoryFilter`); no key
  is wired to it — pressing `/` in the PR section does nothing (the shell's
  filter action targets the work-item list only). Verify `/` is a harmless
  no-op here and note the gap in findings.
  Result: ______
- [ ] **G6 — open PR detail.** `Enter`/`o` on a PR. Expect a modal titled
  `PR !<id> — q close · d diff · v vote · c reply · x resolve · u reactivate ·
  C complete · A abandon` with: `!<id>  <title>` (+ `  [draft]`),
  `<repo>: refs/heads/<src> → refs/heads/<tgt>   status: <status>   merge:
  <mergeStatus or ?>`, `author: <name>`, and a `Reviewers:` block with one
  `  <glyph> <name>` per reviewer (`✓` approved, `✓~` w/ suggestions, `⧗`
  waiting, `✗` rejected, `·` no vote) plus ` (required)` where applicable.
  Result: ______
- [ ] **G7 — linked work items + threads (FX12, FX11).** Same dialog:
  `Linked work items: #<id>, …` when present; `── Description ──`; and
  `── Threads (N unresolved) ──` where each thread renders
  `  #<threadId> [Active|Fixed|…] [<file path>:<line>]` (file/line only for
  line-anchored threads) with non-system comments as `      <author>: <text>`.
  N counts **active, non-system** threads. Cross-check N with the web UI.
  Result: ______
- [ ] **G8 — vote: all five values.** Press `v`. Expect a `vote` dialog with
  exactly the buttons `approve`, `approve w/ suggestions`, `wait for author`,
  `reject`, `reset`. Choose `approve` → message `voted: approve` and your
  reviewer glyph becomes `✓` after the auto-reload. Repeat over the five
  values (finish with `reset` → glyph `·`, message `voted: reset`; reset
  returns the vote to "no vote" so the PR re-enters your review queue).
  Result: ______
- [ ] **G9 — vote reflected in list.** Close the detail. Expect the PR list
  reloads automatically and the vote-summary column reflects the change.
  Result: ______
- [ ] **G10 — reply to a thread (FX11).** Note a thread id `#<n>` from the
  detail body. Press `c`. Expect **two** editor round-trips: first a `.txt`
  buffer — type just the thread id (e.g. `42`), save, quit; then a `.md`
  buffer for the reply text. Save both → `reply posted`, the reply appears
  under that thread after reload. (A non-numeric/empty id silently aborts —
  no message; that's the coded behavior.)
  Result: ______
- [ ] **G11 — resolve thread.** Press `x`, enter the thread id in the editor.
  Expect `thread resolved`, the thread's status shows `[Fixed]`, and the
  unresolved count decrements.
  Result: ______
- [ ] **G12 — reactivate thread.** Press `u`, same id. Expect
  `thread reactivated`, status back to `[Active]`, count increments.
  Result: ______
- [ ] **G13 — complete with strategy prompt (FX9, sacrificial).** Press `C`.
  Expect a `complete PR` dialog `merge strategy:` with buttons
  `noFastForward`, `squash`, `rebase`, `rebaseMerge`; picking one raises a
  second confirmation `Complete with <strategy>?` with `cancel`/`complete`.
  Cancel first — nothing happens. Re-run and confirm → `PR completed`; the PR
  merges in the web UI with that strategy. The source branch is **not**
  deleted (`deleteSource: false` is hardcoded). On a policy-blocked PR expect
  `failed: <ADO policy message>` instead.
  Result: ______
- [ ] **G14 — abandon with confirm (FX9).** On the other sacrificial PR press
  `A`. Expect `abandon PR` / `Abandon this pull request?` with
  `cancel`/`abandon`. Cancel → no change. Confirm → `PR abandoned`; verify in
  the web UI (and reactivate it there afterwards if wanted).
  Result: ______
- [ ] **G15 — verbs are detail-only.** In the PR **list**, `v` shows no dialog
  (the list scope binds it but nothing dispatches it) — expect a no-op, not a
  crash. Same for `c`/`s`/`a` in the work-item list. Known v1 shape; note it.
  Result: ______

### H. Diff review (FX10)

Open the FX10 PR's detail and press `d`.

- [ ] **H1 — dialog opens.** Title: `diff review !<id> — q close · Tab
  files/diff · c comment · [ ] next/prev file`. Left ~28%: changed-file list;
  right: header + unified diff of the first file. While loading:
  ` loading diff…`.
  Result: ______
- [ ] **H2 — file list glyphs.** Each file is `<glyph> <path>` with `+` add,
  `-` delete, `»` rename, `~` edit; paths longer than 34 chars shorten to
  `…<last 33 chars>`.
  Result: ______
- [ ] **H3 — diff header + coloring.** Header: ` <path>   +<adds> -<dels>`.
  Body rows: gutter `<●| ><old#,4> <new#,4> <+|-| >` then the line text.
  Added lines carry a full-width dark-green tint (`#123a12`), removed lines
  dark red (`#3a1212`), context lines the normal background; the `+`/`-` sign
  renders bright green/red. On a non-truecolor terminal the tints degrade to
  the nearest palette color but the sign column still distinguishes lines.
  Cross-check `+/-` counts against the web UI's diff (small cosmetic
  differences in hunk shaping are expected — client-side DiffPlex vs. server).
  Result: ______
- [ ] **H4 — intra-line word emphasis.** Find a modified line pair (removed +
  added versions of the same line). Expect only the changed words carry a
  brighter emphasis tint (`#1e6b1e` on added / `#6b2020` on removed) while
  the unchanged words keep the plain line tint. A **totally rewritten** line
  (>60% changed on either side) intentionally shows NO word emphasis — plain
  add/remove, not confetti.
  Result: ______
- [ ] **H5 — syntax highlighting.** On a `.cs` (or `.js/.jsx/.ts/.tsx/.mjs/
  .cjs/.json/.py`) file: keywords, identifiers, strings, numbers, comments,
  operators render in distinct theme colors (e.g. keyword `var` vs identifier
  differ). Any other extension (e.g. `.md`, `.txt`) falls back to plain — one
  foreground, still diff-tinted.
  Result: ______
- [ ] **H6 — rename-aware diff.** Select the rename-with-edit file (`»`
  glyph). Expect a real diff of old-path→new-path content: header shows e.g.
  `+1 -1` for a one-line edit — **not** a whole-file `+N -0` add.
  Result: ______
- [ ] **H7 — navigation.** `]`/`[` select next/previous file (clamped at the
  ends — no wrap, no crash); `j`/`k` move within the focused pane; `Tab`
  toggles focus between file list and diff pane; `Enter` on the file list
  opens the highlighted file. Reviewing position: after adding a comment to
  the same file the selected line is preserved; switching files resets to the
  top.
  Result: ______
- [ ] **H8 — line comment, right side.** Move the diff cursor onto an
  **added** line, press `c`, write a comment in the editor, save.
  Expect `line comment added` and a `●` appears in that row's gutter. In the
  **web UI**, the thread must anchor to exactly that line on the **new/right**
  side of the file. Repeat on a **context** line (also right side).
  Result: ______
- [ ] **H9 — line comment, left side.** Repeat on a **removed** line.
  Expect the web UI anchors it to the old/**left** side at the old line
  number. Existing threads (FX11 + the ones just created) show `●` markers on
  their lines; the new threads also appear in PR detail (G7) with
  `[<path>:<line>]`.
  Result: ______
- [ ] **H10 — comment failure surface.** (Optional) Force a failure (e.g.
  drop network before saving). Expect `comment failed: <message>`.
  Result: ______
- [ ] **H11 — binary / large-file guards.** Select the binary file. Expect an
  empty diff pane and header ` <path>   +0 -0  (binary)` (NUL-byte
  heuristic). A >50,000-line file shows `  (too large)`. `c` on an empty
  guarded diff does nothing (no editor opens).
  Result: ______
- [ ] **H12 — no-iterations edge.** (Optional, if you can produce a PR with
  no iterations) Expect header ` error: this pull request has no iterations
  to diff`.
  Result: ______
- [ ] **H13 — CJK/emoji observation (NOT a bug).** Select the file with CJK/
  emoji on changed lines. Expected *documented* imperfections (ADR 0010): the
  full-width tint may stop short of the right edge on wide-char lines and
  horizontal scroll bounds may be slightly off; a word boundary through an
  emoji may render `U+FFFD`. Verify only that there is **no crash, no torn
  rows, no misordered text** — anything beyond the cosmetic issues above IS a
  bug.
  Result: ______
- [ ] **H14 — close.** `q`/`Esc` closes the diff dialog back to PR detail;
  `q` again to the list. No residual artifacts.
  Result: ______

### I. Robustness

- [ ] **I1 — human-readable errors everywhere.** Across all failures seen so
  far: every error is one readable line (ADO's own `message` field where
  available, else `Azure DevOps returned <code> <reason>`); never a stack
  trace, never a raw JSON dump — on screen or in `:messages`.
  Result: ______
- [ ] **I2 — network drop.** Disconnect networking, press `r` in each list.
  Expect ` error: <HttpRequestException message>` in the header /
  `failed: …` in dialogs; the app stays responsive; reconnect + `r` recovers.
  Result: ______
- [ ] **I3 — permission denied.** Attempt a mutation you lack rights for
  (e.g. vote/complete in a project where you're a Reader). Expect
  `failed: <ADO authorization message>`; no crash.
  Result: ______
- [ ] **I4 — quitting from every screen.** Verify a clean path out from: main
  list (`:q`), work-item detail (`q`), PR detail (`q`), diff review (`q`),
  help/messages dialogs (`q`), palette (`Esc`), filter (`Esc`), and while a
  list is still loading. Terminal state must be restored after exit (echo,
  cursor, no alternate-screen residue).
  Result: ______
- [ ] **I5 — resize.** Resize the terminal (narrow, wide, very short) with
  the list, a detail dialog, and the diff review open. Expect relayout
  without crash or duplicated chrome; long rows clip rather than wrap.
  Result: ______
- [ ] **I6 — double `--context` fast-fail vs. TUI.** `cobalt --context alt`
  with the alt context valid: TUI starts directly in `ctx:alt` (status line)
  and lists that project's data.
  Result: ______
- [ ] **I7 — rapid input.** Mash `j`/`k`/`Tab`/`1`/`2`/`r` quickly, switch
  sections during a load. Expect no crash and no stuck "loading…" (a canceled
  load on section switch is expected and silent).
  Result: ______

---

## 6. Known limitations — do NOT log these as bugs

From the ADRs and CHANGELOG (plus code-verified v1 gaps). Observe and move on;
only regressions *beyond* these descriptions are findings.

1. **Wide characters (CJK, emoji) in diffs** — the diff-state tint can stop
   short of the row edge and horizontal scroll bounds are slightly off on
   lines with double-width runes; cosmetic only, no crash (ADR 0010).
2. **Surrogate pairs** — a word-diff/token boundary can split an emoji and
   render `U+FFFD` on that slice (ADR 0010).
3. **Line-local syntax highlighting** — the interior of `/* … */` block
   comments and multi-line/triple-quoted/verbatim strings tokenizes per line
   as code (ADR 0010).
4. **Line comments anchor to the latest PR iteration only**; the diff is
   computed client-side and may differ cosmetically from the server's
   (rename/whitespace hunk heuristics) (ADR 0008). Cross-iteration thread
   tracking is post-v1.
5. **Keybindings are fixed in v1** — no remapping config.
6. **`$EDITOR` needs a real interactive terminal** — headless/pipe sessions
   can't do the suspend/resume hand-off (ADR 0009). Cancellation is not
   observed while the editor runs; it always runs to completion.
7. **az-login tenant must match the ADO org's tenant** — otherwise ADO
   answers 203 and cobalt reports the "right tenant?" message (ADR 0003).
   Conditional-access tenants may block the browser flow; use the az path.
8. **`:ctx NAME` switches chrome only** — the live connection is not rebuilt
   in v1 (`CobaltShell` M3 comment); use `cobalt --context NAME`.
9. **PR repository filter is not wired to a key** — `/` does nothing in the
   PR section (view-model only).
10. **Work-item title/priority/story-points have no edit keybinding** in the
    detail dialog (display only); complete never deletes the source branch.
11. **WIQL terminal-state list is fixed** to Closed/Done/Removed/Completed —
    custom processes with other closed-state names will still list those
    items.
12. **HTML→Markdown is lossy on rich HTML** (tables, inline styles) — the
    `⚠ rich HTML` warning is the contract; degradation after an `e` edit on
    such items is expected.

## 7. Results summary & sign-off

| Section | Items | Pass | Fail | Skipped |
|---|---|---|---|---|
| A. CLI basics | 7 | | | |
| B. Config | 9 | | | |
| C. Auth | 7 | | | |
| D. Shell & vim | 15 | | | |
| E. Work items | 14 | | | |
| F. Editor round-trip | 6 | | | |
| G. Pull requests | 15 | | | |
| H. Diff review | 14 | | | |
| I. Robustness | 7 | | | |
| **Total** | **94** | | | |

**Blocking issues found** (id, one line each):

-
-

**Non-blocking findings / gaps confirmed** (e.g. D4 movement keys, G5 repo
filter, D9 ctx switch):

-
-

**If it breaks, capture this** for each failure: the checklist id; exact
reproduction steps (keys pressed, in order); the on-screen text (message bar
and any dialog, verbatim); the full `:messages` history (or stderr + exit code
for CLI items); the work item / PR / thread id and repository; terminal
emulator + `$TERM` + window size; and whether it reproduces after `r` or a
restart.

Sign-off: I certify the checklist above was executed against a real Azure
DevOps organization and the results recorded truthfully.

| | |
|---|---|
| Tester | |
| Date | |
| Verdict (ship / fix-first) | |
