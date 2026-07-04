# Cobalt — Azure DevOps TUI: Specification (v1)

A fast, vim-flavored terminal UI for Azure DevOps, built for a neovim-centric
workflow: triage and update your work items, and review, comment on, and vote
on pull requests — without leaving the terminal.

This spec is the outcome of the requirements grilling on 2026-07-04.

## 1. Scope decisions (agreed)

| Decision | Choice |
|---|---|
| Target platform | Azure DevOps **Services** (cloud, `dev.azure.com`) only. REST API 7.2. No on-prem Server support in v1. |
| Authentication | Entra ID only — no PATs. Credential chain: reuse an existing `az login` session first, fall back to interactive browser sign-in. Tokens cached persistently (OS keychain-backed where available). |
| TUI framework | Terminal.Gui **v2** (stable 2.4.x). |
| Language/runtime | Latest stable C# on .NET 10 (LTS). Nullable enabled, analyzers on, `System.Text.Json` source generation. |
| API access | Hand-rolled typed REST client over the Azure DevOps REST API. The legacy `Microsoft.TeamFoundationServer.Client` package is deliberately **not** used (Newtonsoft-era, poor fit for modern .NET). |
| v1 shape | Both work items and PRs ship in v1 as thin-but-usable slices; PR review includes full in-TUI diff + line comments. |
| Config | Multiple orgs/projects ("contexts") in one config file, with fast in-app switching and a CLI flag. |
| Packaging | `dotnet tool install -g cobalt` (dotnet global tool). Native AOT single binary is a later goal, not v1. |
| Vim depth | Navigation + verbs (see §5). Full modal emulation (counts, marks, visual mode) is out of scope for v1. |
| Long-text editing | Hybrid: quick single-line edits in-TUI; `e` escalates to `$EDITOR` (neovim) round-trip — suspend TUI, edit temp file, resume on save. |

## 2. Work items

### Views
- **My work items** (the daily driver): flat list of items assigned to `@Me`,
  excluding terminal states by default. Columns: ID, type, title, state,
  iteration, updated. Filterable with `/`, sortable via command palette.
- Sprint board, saved queries, and backlog hierarchy are explicitly **post-v1**.

### Detail view
- Title, type, state, assignee, iteration/area, tags, priority, story points,
  description (HTML rendered to readable terminal Markdown), and the full
  comment/discussion thread.

### Mutations (all v1)
- **State transitions** — offered states come from the process template
  (`workitemtypes/{type}/states`), not a hardcoded list.
- **Comments** — read thread, add new comment (in-TUI one-liner or `$EDITOR`).
- **Assignee & fields** — reassign (identity picker), edit title, tags,
  priority, story points.
- **Description editing** — ADO stores HTML; Cobalt converts HTML → Markdown
  for editing in `$EDITOR` and Markdown → HTML on save. Known limitation:
  conversion is lossy on complex HTML (tables with styling, embedded images);
  Cobalt warns before overwriting a description it couldn't round-trip cleanly.

## 3. Pull requests

### Lists (three tabs)
1. **Review queue** — PRs where I'm a reviewer and my vote is pending (default tab).
2. **Mine** — PRs I authored: vote status per reviewer, unresolved thread count.
3. **Active** — all active PRs in the current project, filterable by repository.

### PR detail
- Description, source/target branches, reviewers + votes, merge status,
  linked work items, and all comment threads (reply, resolve/reactivate).

### Review (the flagship)
- **File tree** of changed files for the latest iteration.
- **Diff pane**: unified diff computed client-side from base/target file blobs,
  colored adds/removes, intra-line highlighting. Syntax highlighting of code
  is a stretch goal, not v1.
- **Line comments**: start a comment thread anchored to a line
  (`threadContext` on the right/left file), reply to and resolve existing
  threads; thread markers shown in the gutter.
- v1 comments anchor to the **latest iteration**; cross-iteration thread
  tracking is post-v1.

### Actions
- **Vote**: approve / approve with suggestions / wait for author / reject / reset.
- **Complete** (with merge-strategy prompt and confirmation) and **Abandon** —
  both gated behind explicit confirmation.

## 4. Auth & config

- Credential chain: `AzureCliCredential` → `InteractiveBrowserCredential`
  (Azure.Identity), requesting the Azure DevOps resource scope
  (`499b84ac-1321-427f-aa17-267ca6975798/.default`).
- `cobalt auth status` / `cobalt auth login` CLI verbs for diagnostics.
- Config file: `~/.config/cobalt/config.toml` (XDG-aware; `%APPDATA%` on Windows):

```toml
default_context = "work"

[contexts.work]
organization = "https://dev.azure.com/contoso"
project = "Platform"

[contexts.oss]
organization = "https://dev.azure.com/contoso"
project = "Tools"
```

- In-app context switcher (`:ctx` / picker) and `cobalt --context oss`.

## 5. Keybindings (vim layer)

Global: `j/k` move, `h/l` collapse/left · expand/right, `gg`/`G` top/bottom,
`Ctrl-d`/`Ctrl-u` half-page, `/` filter-as-you-type, `:` command palette,
`?` help overlay, `r` refresh, `q` back/close, `:q` quit,
`Tab`/`S-Tab` next/prev tab, `Ctrl-h`/`Ctrl-l` pane focus, `1`/`2` work-items/PRs section.

Contextual verbs: `o`/`Enter` open, `c` comment, `e` edit in `$EDITOR`,
`s` change state, `a` assignee, `t` tags, `v` vote, `x` resolve thread,
`u` reactivate thread, `yy` yank item URL/ID to clipboard, `gx` open in browser.

Bindings are hardcoded in v1 behind a single keymap table (one place to edit);
user-remappable keymaps in config are post-v1.

## 6. Non-functional

- **Responsiveness**: all network I/O async off the UI thread; optimistic
  list updates where safe; explicit loading states — the UI never blocks.
- **Rate limiting**: honor `Retry-After`/429 with backoff (Microsoft.Extensions.Http.Resilience).
- **Errors**: human-readable error bar with `:messages`-style log view; never a raw stack trace.
- **Testability**: view-model layer independent of Terminal.Gui; REST client
  tested against a fake `HttpMessageHandler`; HTML↔Markdown conversion snapshot-tested.
- **Distribution**: NuGet package `cobalt` (dotnet global tool), CI-built.

## 7. Out of scope for v1 (parked)

Sprint/kanban board · saved queries/WIQL browser · backlog hierarchy ·
PR creation · pipelines/builds · notifications/polling daemon · syntax
highlighting in diffs · cross-iteration thread tracking · remappable keys ·
Native AOT binaries · Azure DevOps Server (on-prem).

**Deferred during implementation** (originally in §3, moved out to hit v1):
word-level **intra-line diff highlighting** (v1 colours whole added/removed
lines) and **rename-aware diffs** (a rename renders as a whole-file add). Both
tracked in the CHANGELOG.
