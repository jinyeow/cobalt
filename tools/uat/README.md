# UAT route prober (Harness A)

A small, repeatable console harness that exercises the **live Azure DevOps routes**
cobalt depends on but which the headless CI build can only verify "by shape". It boots
the real config + Entra token (same as `cobalt` itself) and calls the `Cobalt.Core` ADO
clients directly — no TUI, no app loop, no driver — so it runs in any terminal and prints
structured results a human can eyeball.

It is deliberately **not** part of `Cobalt.slnx`, so it never affects CI build/pack.

## Run

```sh
dotnet run --project tools/uat                 # uses default_context from config
dotnet run --project tools/uat -- --context oss
```

Prereqs are the same as running cobalt: a valid `config.toml` and `az login` (or the
cached interactive auth record).

## What it probes (and why)

| Probe | Route | UAT item it de-risks |
|-------|-------|----------------------|
| org-wide work items | project-segment-less `_apis/wit/wiql` + `workitemsbatch` | items assigned across all projects; project column when rows span >1 project |
| cross-project WI metadata | `.../{project}/_apis/wit/workitemtypes/{type}/states` for an item **outside** the context project | the round-2 HIGH fix: drill-in must use the item's OWN project (no 404, correct states) |
| my teams | `_apis/teams?$mine=true` | Team-tab membership resolution |
| Team-tab PR routes | `searchCriteria.reviewerId={teamGuid}` + active-PR union, with a decisive cross-check | team-as-reviewer + teammate-authored halves |

The Team probe cross-checks the `reviewerId={teamGuid}` result against the teams actually
seen as reviewers on the org-wide active-PR set, so a genuine zero (team isn't a reviewer)
is distinguished from a broken route (team IS a reviewer but the query returns nothing).

## Findings snapshot (2026-07-07, HollardInsuranceRetail / TSC Cloud DevOps)

All three P1 data unknowns verified clean against the live org:

- **org-wide WI** — 84 rows across 3 projects (TSC Cloud DevOps, TSC Change Control,
  Technology Initiatives); multi-project → project column path exercised.
- **cross-project WI metadata** — item in *TSC Change Control* (≠ context project)
  returned its RFC-specific states and detail with no 404.
- **Team-tab routes** — `reviewerId={teamGuid}` authoritative (41 == cross-check for one
  team; 17 for another); permission-group teams legitimately return 0.
