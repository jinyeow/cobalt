# 0011 — Cross-project (org-wide) PR scope, with per-PR-project drill-in

Status: Accepted · Date: 2026-07-06

## Context

Cobalt originally listed pull requests for a single project — the context's
`project`. In practice a reviewer's queue spans every project in the org: the PRs
awaiting my vote, the ones I authored, and the active set all live wherever the
work happens. Scoping to one project hid most of a reviewer's real workload and
forced a context switch per project.

The user's product decision: **the default PR scope is the whole organization**,
with a runtime toggle back to a single project. Single-project behaviour must stay
reachable (some users want a focused view; some orgs are effectively one project).

Two facts about the Azure DevOps REST surface shaped the design:

- `GET _apis/git/pullrequests/{id}` (by id) and the reviewer routes are **org-level
  and documented** — `GetPullRequestAsync` already uses the by-id route successfully,
  so org-level routing against this API is proven.
- `GET _apis/git/pullrequests?searchCriteria…` **without** a project segment returns
  the org-wide list, but that list form is **not formally documented** (only the
  per-project `{project}/_apis/git/pullrequests` form is). The by-id and reviewer
  org-level routes are documented; the list one is inferred.

## Decision

- **Add a `PrScope { Project, Org }` enum (Cobalt.Core) and an optional per-context
  `pr_scope` TOML key.** Absent ⇒ `Org` (the product default). `Org` needs only the
  org URL (already required); `Project` reuses the required `project` key — so every
  existing config stays valid and both scopes always validate.

- **`GitApi.ListPullRequestsAsync` takes an explicit `PrScope`.** Org scope issues
  `_apis/git/pullrequests?…` (no project segment); Project scope keeps
  `{project}/_apis/git/pullrequests?…`. The prefix is a single local switch, and a
  code comment flags the undocumented org-list route as UAT-validated — so swapping
  to a documented per-project fan-out later is a localized change, not a rewrite.

- **Drill-in uses each PR's own project, never the context's.** Under org scope a
  list row can belong to any project, so threads / vote / iterations / changes /
  file content / line comments / web URLs must target that PR's project. `GitApi`'s
  repo-scoped methods gained an **optional** `project` parameter (defaulting to
  `context.Project`, preserving all existing call sites); `PullRequestStoreAdapter`
  and the detail/diff view-models thread the selected PR's `ProjectName` through.
  `AdoUrls.PullRequest` likewise takes the PR's project (falling back to the context
  project when blank).

- **The DTO carries the project.** `GitRepositoryDto` gains a `TeamProjectRefDto`
  (`project`), surfaced as `PullRequest.ProjectName` (falling back to the context
  project when the response omits it — project-scoped responses often do). A
  `CreationDate` is also mapped (`PullRequestDto` → `PullRequest.CreationDate`) for a
  later PR-age column. Both are System.Text.Json source-generated like every other
  field; the nested `TeamProjectRefDto` is reached through the existing serializable
  graph, so no new `[JsonSerializable]` root is required.

- **A `:scope [org|project]` palette command flips the scope at runtime.** Bare
  `:scope` prints the current value; `:scope org|project` sets it, repoints the
  adapter's mutable `Scope`, and reloads the list. The active scope shows in the
  status line (`scope:org` / `scope:project`).

## Update — work items honor scope, plus `:done` / `:project` (P2)

The same `:scope` now drives the **work-item** list, not just PRs:

- **Org-wide WIQL uses the project-less route.** `POST _apis/wit/wiql` (no project
  segment) runs the query across every project; `{project}/_apis/wit/wiql` narrows to
  one. `WorkItemsApi.QueryMyWorkItemsAsync` takes a `PrScope` and drops the project
  prefix under `Org` (and threads the same decision into `workitemsbatch`, which is
  likewise org-capable). The WIQL two-step (ids → `workitemsbatch`) is documented; the
  Query By Wiql REST API accepts both the org- and project-level route. A
  `[System.TeamProject] = '…'` clause narrows within either route, so an explicit
  `:project` filter forces the org route even under project scope (so it can reach a
  project other than the context's). `WorkItem` now surfaces `TeamProject` and the
  list request asks for `System.TeamProject`.
- **`WorkItemQuery` + `WiqlBuilder` (pure).** The WIQL is built by a pure
  `WiqlBuilder.MyItems(WorkItemQuery)` (string-exact unit tested), keeping the
  route/HTTP concerns in `WorkItemsApi`. `WorkItemQuery(IncludeCompleted, Project)`
  carries the two tunables.
- **`:done` (server-side) and `:project` (server + client).** `:done show|hide`
  toggles `IncludeCompleted`; the default excludes
  `Closed/Done/Completed/Resolved/Removed` (`Resolved` was previously missing from
  the WIQL). Because the client can only narrow rows it already fetched,
  hide-completed must stay WIQL-side. `:project NAME` narrows the work-item list
  server-side (`[System.TeamProject]`) and the PR list client-side (on
  `PullRequest.ProjectName`, mirroring the existing `RepositoryFilter`). Both flow
  through `ShellViewModel` events (`DoneFilterChanged`, `ProjectFilterChanged`) like
  `:scope`.
- **Risk: the org-wide *WIQL/workitemsbatch* route without a project segment is
  validated only by URL/body shape here** (the hand-rolled client can unit-test the
  request, not a live org). It is implemented to the documented shape and flagged for
  UAT against a real org, same posture as the org PR-list route above.

## Consequences

- The optional `project` parameters keep every existing `GitApi` construction and
  test compiling; only the two methods that gained a *required* new parameter
  (`ListPullRequestsAsync`'s `PrScope`, and the view-model interfaces' `project`)
  needed call-site updates.
- `$top` was raised from 100 to 200 to accommodate the wider org list; paging beyond
  that (`$skip`) is deferred — it is a localized follow-up.
- **Risk: the org-wide *list* endpoint is undocumented.** It is exercised in UAT
  against a real org. If it ever regresses, the per-project fan-out fallback is a
  localized change behind `ListPullRequestsAsync` (the scope switch is the only
  branch that would change).
- Displaying a project column and richer per-row formatting is deferred; the model
  now carries `ProjectName`/`CreationDate` so that work is purely presentational.
