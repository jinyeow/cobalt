# Cobalt — Implementation Plan

Companion to [SPEC.md](./SPEC.md). Six milestones, each independently shippable
and demoable. Estimated relative sizes in ⬤ (1–5).

## Status

- [x] M0 — Scaffold & CI (done 2026-07-04; package id is `cobalt-tui`, see ADR 0005)
- [x] M1 — Auth + config + API foundation (done 2026-07-04; M0 review fixes folded in)
- [ ] M2 — TUI shell + vim input layer
- [ ] M3 — Work items slice
- [ ] M4 — PR lists, threads, votes
- [ ] M5 — Diff review + line comments
- [ ] M6 — Polish & release

## Architecture

```
src/
  Cobalt.Core/          # no UI dependencies — fully unit-testable
    Auth/               # Azure.Identity credential chain, token provider
    Config/             # contexts, TOML config load/save, XDG paths
    Ado/                # typed REST client (one class per API area)
      WorkItemsApi      # WIQL, batch get, JSON Patch update, comments, states
      GitApi            # PRs, threads, reviewers/vote, iterations, item blobs
      IdentityApi       # connectionData (who am I), identity picker search
    Models/             # DTOs, System.Text.Json source-gen context
    Text/               # HTML <-> Markdown conversion, diff computation (DiffPlex)
  Cobalt.Tui/           # Terminal.Gui v2 application
    App/                # bootstrap, DI wiring, theming, error/message bar
    Input/              # vim keymap table + key router (pending-key 'gg' support)
    Screens/            # WorkItemList, WorkItemDetail, PrList, PrDetail, PrReview(Diff)
    ViewModels/         # screen state + commands, no Terminal.Gui types
    Editor/             # $EDITOR suspend/round-trip service
  Cobalt/               # exe entry: CLI verbs (auth, --context), launches TUI; packs as dotnet tool
tests/
  Cobalt.Core.Tests/    # xUnit; fake HttpMessageHandler; conversion snapshots
  Cobalt.Tui.Tests/     # view-model + keymap routing tests
```

Key libraries: `Terminal.Gui` 2.4.x · `Azure.Identity` · `Microsoft.Extensions.Http.Resilience`
· `DiffPlex` · `Markdig` + `ReverseMarkdown` · `Tomlyn` · `System.CommandLine` (CLI verbs).
MVVM-ish: view-models own all state and talk to `Cobalt.Core`; Terminal.Gui views bind to them.

## Milestones

### M0 — Scaffold & CI ⬤
- Solution, projects, `Directory.Build.props` (net10.0, nullable, implicit usings,
  analyzers, warnings-as-errors), `.editorconfig`, dotnet-tool packing metadata.
- GitHub Actions: build + test on push/PR; release workflow stub (pack + publish on tag).
- **Demo**: `dotnet tool install -g --add-source ./nupkg cobalt-tui && cobalt --version`.

### M1 — Auth + config + API foundation ⬤⬤⬤
- Config load/save, context model, `--context` flag.
- Credential chain (az CLI → interactive browser) with persistent token cache;
  `cobalt auth login` / `cobalt auth status`.
- `AdoHttpClient`: base-address per org, bearer injection, resilience pipeline
  (429/`Retry-After`, transient retry), JSON source-gen, typed error envelope.
- `IdentityApi.GetConnectionData()` — resolves authenticated user id (needed for
  `@Me` queries and the reviewer-vote PUT).
- **Demo**: `cobalt auth status` prints who you are in each configured context.

### M2 — TUI shell + vim input layer ⬤⬤⬤
- Terminal.Gui v2 bootstrap: top-level layout (tab strip, content, status/error bar),
  theme, async task→UI marshaling helper.
- Keymap table + router: sequences (`gg`, `yy`, `gx`), contextual verb dispatch,
  `/` filter widget, `:` command palette, `?` help overlay generated from the table.
- Context switcher (`:ctx`).
- `$EDITOR` round-trip service (suspend TUI, temp file, resume) — built here so
  M3/M4 just call it.
- **Demo**: navigable empty shell with working palette, help, context switching.

### M3 — Work items slice ⬤⬤⬤⬤
- WIQL "my work items" query + batch fetch → list screen (filter, sort).
- Detail screen: fields, HTML→terminal-Markdown description, comment thread.
- Mutations: state picker (from process-template states), assignee (identity
  search picker), title/tags/priority/points quick-edits, add comment,
  description edit via `$EDITOR` with HTML↔Markdown round-trip + lossiness warning.
- All writes via JSON Patch; optimistic UI update with rollback on failure.
- **Demo**: triage a real work item end-to-end from the terminal.

### M4 — PR lists, threads, votes ⬤⬤⬤
- Three list tabs (review queue / mine / active) over project-level PR search;
  repo filter; vote + unresolved-thread badges.
- PR detail: description, reviewers/votes, merge status, linked work items,
  overview comment threads with reply/resolve/reactivate.
- Actions: vote (all five values), complete (merge-strategy prompt + confirm),
  abandon (confirm).
- **Demo**: process your review queue — read, reply, vote — without a browser.

### M5 — Diff review + line comments ⬤⬤⬤⬤⬤
- Latest-iteration change list → file tree pane.
- Fetch base/target blobs per file, compute unified diff (DiffPlex), render
  with add/remove coloring and intra-line highlights; large-file and binary guards.
- Gutter markers for existing threads; open/reply/resolve threads from the diff;
  new line comments posted with correct `threadContext` (left/right anchoring).
- **Demo**: full code review — diff, line comments, vote — inside Cobalt.

### M6 — Polish & release ⬤⬤
- Empty/loading/error states everywhere; `:messages` log view; startup time pass
  (defer network until first screen paints); README with install + keybinding docs;
  publish `0.1.0` to NuGet via the release workflow.

## Sequencing & risk notes

- M1 and M2 are independent after M0 — can proceed in either order or in parallel.
- **Riskiest item**: M5 line-comment anchoring (`threadContext` semantics across
  left/right buffers). Mitigation: spike this API against a scratch PR during M4.
- HTML↔Markdown lossiness is contained by the warn-before-overwrite guard (M3).
- Terminal.Gui v2 API churn: pin the minor version; wrap all Terminal.Gui usage
  behind the view layer so upgrades touch one project.
- Entra tenants with conditional-access policies may block the public-client
  browser flow; the az CLI credential (first in the chain) is the escape hatch.
