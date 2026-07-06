# 0015 — Team PR tab (raw union of team-reviewed and teammate-authored PRs)

Status: Accepted · Date: 2026-07-06

## Context

The PR section had three tabs — review queue (PRs awaiting my vote), mine (PRs I
authored), active (everything). None of them answers "what is my *team* working
on?": PRs where a team I belong to is a requested reviewer, or PRs a teammate
authored. Reviewers want that shared view without hand-tracking who is on which
team.

Two facts about the Azure DevOps REST surface shaped the design:

- **Team membership is one cheap org-level call.** `GET {org}/_apis/teams?$mine=true`
  (Core / Teams — Get All Teams) returns every team the caller belongs to across the
  whole org, each `WebApiTeam` carrying `id`, `name`, `projectId`, `projectName`. It
  lives on `dev.azure.com`, so it rides the existing org-scoped `AdoHttp` (no vssps /
  Graph host). `GET {org}/_apis/projects/{projectId}/teams/{teamId}/members` returns
  each team's members (`TeamMember.identity → { id, displayName, uniqueName }`), one
  call per team.
- **A team is an identity, so `searchCriteria.reviewerId={teamGuid}` works.** The
  Get-Pull-Requests documentation's own example shows a team reviewer with
  `isContainer:true` and the team's identity guid — i.e. the existing reviewer route
  filters by a *team* the same way it filters by a user. This is documented only *by
  example* (like the org list route in ADR 0011), so it is flagged for UAT.

## Decision

- **The Team tab is a RAW UNION.** It shows the union of (a) active PRs where a team
  the user belongs to is a requested reviewer, and (b) active PRs authored by a
  teammate. It does **not** exclude the user's own PRs, nor PRs already visible on
  other tabs. Dedupe is by `PullRequestId` **only** — a PR that matches both halves
  appears once. Custom / configurable views (exclude-mine, per-team) are deferred to
  v2. This was the confirmed product decision; the simplicity keeps the aggregation
  cheap and predictable.

- **`PrListFilter` gains `Team`; tab order is `[ReviewQueue, Team, Mine, Active]`.**
  Team sits second so the team view is one `Tab` press from the review queue.

- **A new `TeamsApi` (Cobalt.Core) wraps the two Teams routes.** `GetMyTeamsAsync`
  → `AdoTeam(Id, Name, ProjectId, ProjectName)`; `GetTeamMembersAsync(projectId,
  teamId)` → `AdoUser` (reusing the existing identity projection). DTOs are
  System.Text.Json source-generated (`TeamsJsonContext`), and errors flow through the
  same `AdoApiException` envelope as every other API.

- **`GitApi.ListPullRequestsForReviewerAsync(reviewerId, scope)` is extracted** from
  the existing review-queue branch (which now delegates to it). It composes
  `searchCriteria.status=active&searchCriteria.reviewerId={guid}` with the org-vs-
  project prefix — a team guid drops straight in, so Team's reviewer half is not
  shoehorned into the `searchCriteria` filter switch.

- **`PullRequest` gains `AuthorId`** (from `dto.CreatedBy?.Id`, defaulting to `""`
  so existing constructions still compile). The wire `CreatedBy.Id` was already
  deserialized; the teammate-authored half matches on it.

- **Aggregation lives in `PullRequestStoreAdapter`, keeping `PrListViewModel` thin**
  (the same shape as the review-queue's client-side "awaiting my vote" refinement).
  The adapter owns a lazily-built, cached `TeamDirectory` (one `$mine=true` call plus
  one members call per team), resolved at most once and reused across reloads —
  memberships change rarely. The Team query is then:
  - **(a) team-as-reviewer:** `T` parallel `ListPullRequestsForReviewerAsync(teamId,
    scope)` calls (`T` = team count, typically 1–5).
  - **(b) teammate-authored:** the single existing Active list (`status=active`,
    `$top=200`), filtered client-side to `AuthorId ∈ TeammateIds`. A per-teammate
    `creatorId` fan-out was rejected as too many requests.
  - Merge, dedupe by `PullRequestId`, sort by `CreationDate` descending.

- **Scope:** under project scope the cached team list is filtered to
  `AdoTeam.ProjectName == context.Project` before the fan-out, and the reviewer-list
  calls use the project prefix as usual. Under org scope every team participates. Any
  failure (including the teams call itself) propagates through the adapter and is
  surfaced by `PrListViewModel.LoadTabAsync`'s existing `AdoExceptions.IsExpected`
  handling — an error pane, not a crash.

## Consequences

- **Risk — `reviewerId={teamGuid}` is documented only by example.** Implemented to
  the documented shape and unit-tested by URL shape; flagged for UAT against a real
  org (same posture as the org list route in ADR 0011).
- **Risk — the teammate-authored half is bounded by `$top=200`** on the single Active
  list. On very large orgs a teammate's PR past the first 200 active PRs would be
  missed. Paging (`$skip`) is the localized follow-up, shared with ADR 0011.
- **Risk — the teams list is likewise bounded by `$top=200`.** `GetMyTeamsAsync` requests
  `_apis/teams?$mine=true&$top=200`; a user who belongs to more than 200 teams across the
  org would have the tail truncated (and those teams' PRs/members omitted from the Team
  tab). Bounded in practice (users are on few teams) and paged by the same `$skip`
  follow-up as the two PR-list routes above.
- **Risk — large teams.** Team-as-reviewer fans out one call per team; a user on many
  teams issues more parallel calls. Bounded in practice (teams per user are few) and
  cheap (each is the existing list route).
- `TeamsApi`, `AuthorId`, and the extracted reviewer method are all additive; no
  existing call site or test needed a signature change beyond the adapter's new
  optional `resolveTeams`/`project` constructor parameters.
- Custom / per-team / exclude-mine views are explicitly deferred to v2.
