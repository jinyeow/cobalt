# 0008 — Client-side unified diff and line-comment anchoring

Status: Accepted · Date: 2026-07-04

## Context

PR review (SPEC §3, the flagship feature) needs a diff view and the ability to
comment on specific lines. Azure DevOps does not return a ready-made unified
diff; it exposes iterations, per-iteration change lists, and file content at a
commit. Line comments are created as threads carrying a `threadContext`
(`filePath` plus `rightFile*`/`leftFile*` line positions). This anchoring was
called out in the plan as the riskiest single item.

## Decision

- Compute the diff **client-side**: fetch the changed file's content at the
  iteration's base commit (`commonRefCommit`) and source commit
  (`sourceRefCommit`), then run `DiffPlex` (`InlineDiffBuilder`) to a unified
  line model. Added files diff against empty base; deleted files against empty
  source; a missing blob (404) is treated as empty.
- `DiffService` is pure and unit-tested (context/add/remove line numbering,
  addition/deletion counts, binary detection via NUL byte, large-file cap,
  trailing-newline normalization to git-style line semantics).
- **Line comments** anchor on the **right (new) side** for context/added lines
  and the **left (old) side** for removed lines, using a one-based
  `line`/`offset` position in `threadContext`. v1 anchors against the latest
  iteration only; cross-iteration thread tracking is post-v1.
- Per-file diffs are fetched lazily and cached; threads are mapped to lines by
  `filePath` + right line for gutter markers.

## Consequences

The diff surface is fully testable without a live service and independent of any
ADO diff endpoint. Cost: two blob fetches per file (mitigated by lazy load +
cache), and client-side diffs can differ cosmetically from the server's (e.g.
rename/whitespace heuristics). Binary and oversized files are skipped with a
visible marker rather than mis-rendered.
