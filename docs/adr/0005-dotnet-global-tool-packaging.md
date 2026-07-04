# 0005 — Distribute as a dotnet global tool

Status: Accepted · Date: 2026-07-04

## Context

Options: dotnet global tool (needs .NET runtime, trivial to publish), Native
AOT single binary (instant start, no runtime, but Terminal.Gui + Azure.Identity
AOT compatibility is unproven and would burn v1 time), or both.

## Decision

v1 ships as a dotnet global tool: `dotnet tool install -g cobalt-tui`, command
name `cobalt`, packed from `src/Cobalt`. The package id is `cobalt-tui` because
`cobalt` is already taken on nuget.org by an unrelated (non-tool) package.
Native AOT binaries are a post-v1 goal; ADR 0002's source-generated
serialization keeps that door open.

## Consequences

One-command install/update for the target audience (C# developers who have the
SDK anyway). CI packs on every build so packaging never rots.
