# 0002 — Hand-rolled REST client over Azure DevOps REST API 7.2

Status: Accepted · Date: 2026-07-04

## Context

Two ways to talk to Azure DevOps from .NET: the official
`Microsoft.TeamFoundationServer.Client` packages, or direct REST calls. The
official packages are .NET-Framework-era in design, depend on Newtonsoft.Json,
drag in a large dependency graph, are awkward with modern `TokenCredential`
auth, and are effectively in maintenance mode. Cobalt targets Azure DevOps
Services only (cloud), so we control the API version we call (7.2).

## Decision

Build a thin, typed REST client in `Cobalt.Core/Ado` on `HttpClient`:

- One API class per area (`WorkItemsApi`, `GitApi`, `IdentityApi`) sharing a
  configured `HttpClient` (bearer injection + resilience handlers).
- DTOs under `Models/` serialized with `System.Text.Json` source generation.
- Work-item writes use JSON Patch (`application/json-patch+json`) documents
  built by our own small builder — no external JSON Patch dependency.

## Consequences

We own every endpoint we touch (small, auditable surface; testable against a
fake `HttpMessageHandler`), at the cost of writing DTOs by hand and tracking
any REST contract changes ourselves. AOT compilation stays feasible later
because serialization is source-generated.
