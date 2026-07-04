# 0006 — TDD workflow, xunit v3, central package management

Status: Accepted · Date: 2026-07-04

## Context

The project is built milestone-by-milestone under review by independent
reviewers after each milestone. Tests are the contract each milestone is
judged against.

## Decision

- **TDD**: behavior lands test-first — write the failing test, make it pass,
  refactor. Logic lives in `Cobalt.Core` and `ViewModels/` precisely so it can
  be driven by tests (ADR 0004). Network code is tested against fake
  `HttpMessageHandler`s; no live-service tests in CI.
- **xunit v3** with `Microsoft.NET.Test.Sdk` runner integration.
- **Central package management** (`Directory.Packages.props`): every dependency
  version pinned in one file. `TreatWarningsAsErrors` everywhere.

## Consequences

Reviewers can trust green CI as a behavioral statement. Version bumps are
single-file diffs. Slight friction adding packages (two files) — acceptable.
