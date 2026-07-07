# 0001 — Record architecture decisions

Status: Accepted · Date: 2026-07-04

## Context

Cobalt is being built quickly against an agreed spec ([docs/SPEC.md](../SPEC.md)).
Decisions made under time pressure are exactly the ones that need a paper trail.

## Decision

Keep lightweight ADRs (this format: Context / Decision / Consequences) in
`docs/adr/`, numbered sequentially, indexed in `README.md`. A decision is
recorded when it constrains future work: a dependency bet, a protocol choice,
a structural boundary. Records are immutable; reversals get a new ADR that
supersedes the old one.

## Consequences

Small ongoing writing cost per milestone; reviewers and future contributors can
audit *why*, not just *what*.
