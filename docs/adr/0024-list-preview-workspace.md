# 0024 — List + live-preview workspace: concrete design

Status: Accepted · Date: 2026-07-23

## Context

ADR 0022 stage B names the flagship: the content area splits into a list (left) and a
preview panel (right) that re-renders the selected item's detail on cursor move. The
roadmap fixed the constraints (async cancel-superseded fetches, reuse of the existing
detail view-models, focus as an explicit workspace concept, responsive collapse,
`Enter` still opens the modal) but left three design forks open: preview visibility,
load depth, and who gets `Tab`. All three were decided 2026-07-20 (the "A/A/A" picks,
recorded here — not reopened). The full proposal this ADR condenses is
`docs/flagship-preview-design.md`.

Process, part of the decision: the stage is design-gated, not the batch TDD machine —
this ADR → throwaway prototype spike (tier-transition and debounce feel; not merged) →
foundation refactors → build → UAT on **both** the `windows` and `dotnet` drivers
(ADR 0016) before the design is trusted. A green headless suite proves the invariants
below; it can never prove the preview *feels* right.

## Decision

### Visibility — always-on (fork A)

The preview is visible by default and updates live on cursor move. A manual toggle is
persisted in `config.toml` as `preview = auto|off` (default `auto`); below the width
threshold, or when toggled off, the list is full-width — exactly today's UX. Shipping
the flagship hidden behind an opt-in would concede the bet; the toggle plus
auto-collapse covers the 80-col and slow-org cases.

Layout comes from a pure shell-wide calculator, `WorkspaceLayout.Compute(width)`,
generalized from the diff dialog's `ResponsiveLayout` — same shape (pure thresholds,
unit-tested, the view applies the result on resize), **separate threshold tables**;
the two surfaces are consumers of a pattern, not one merged function. Starting
geometry (prototype-tunable): ≥ 110 cols → list ~45% (clamped 40–70) + preview;
100–109 → list fixed-min, preview the remainder (may be summary-tier only); < 100 or
toggled off → collapsed, so an 80×24 terminal defaults to today's full-width list.
(Amended 2026-07-23 at implementation: the earlier draft table kept the preview down
to 80 cols while tickets #42/#45 collapsed below ~100 — unified at 100, one named
constant the prototype tunes.)

### Load depth — two-tier (fork B)

- **Tier 1, instant:** painted from data the list row already holds (title, author,
  repo, vote summary, comment count) — zero fetches, zero latency. The pane is never
  blank while the cursor moves.
- **Tier 2, on settle:** the full detail fetch is scheduled only after the cursor
  settles (~200 ms idle, prototype-tunable), driven by an injected `TimeProvider` so
  tests control time. Holding `j` enqueues nothing; tier-2 traffic is bounded by the
  debounce.

Fetch-on-every-move was rejected: 2–3 round-trips per cursor stop
(`PrDetailViewModel.LoadAsync` = PR + threads + policies) hammers the org and
amplifies the load hazard below. The tier transition must not flicker upward — a
prototype question, not a test question.

### Focus and `Tab` (fork C)

A `WorkspaceViewModel` owns `FocusedPane {List, Preview}` — focus is workspace state,
not inferred from Terminal.Gui focus. `C-h`/`C-l` (the existing
`FocusLeft`/`FocusRight` commands) drive it; the shell maps `FocusedPane` to TG
`SetFocus` in one place, keeping routing headless-testable. List-pane focus keeps
`j`/`k` on the cursor; preview-pane focus routes `j`/`k`/`C-d` to preview scroll.

**`Tab` claims pane-focus cycling inside the workspace**, overriding the global
`Tab` = next-sub-tab alias there (the binding table reserved it for exactly this).
`[`/`]` remain the canonical sub-tab keys (ADR 0021). This is a deliberate break of a
shipped alias — noted in the CHANGELOG; the remap config (ADR 0023) is the mitigation
for dissenting muscle memory.

### The load invariant (spec, not fixes)

A pane that loads on cursor-move is exactly the "cursor's file ≠ displayed file" shape
that produced four bugs in PR #11 and forced ADR 0008's invariants. They are adopted
here from day one; an implementation violating any of them is rejected, not patched:

1. **Atomic publish.** Preview state is one
   `sealed record PreviewState(ItemKey Key, Detail Detail)` published in a single
   reference write — never two adjacent property assignments (precedent:
   `PrDiffViewModel.DiffState`). A torn "PR #12's title over PR #15's reviewers" is
   impossible by construction.
2. **Keyed on the displayed item, not the moving cursor.** The pipeline captures the
   target `ItemKey` at schedule time and publishes only if a monotonic sequence stamp
   says the request is still the newest (the `_loadSeq` supersede pattern proven in
   both list VMs). A stale completion is dropped whole.
3. **Single-flight, cancel-superseded, faults observed.** One in-flight preview fetch;
   scheduling a new key cancels the previous CTS (linked to the workspace-lifetime
   token), and the superseded task's fault is observed so it can't reach the crash-log
   hook (ADR 0013). The ADR 0008 token trap (joiner bound to the starter's token) is
   avoided by construction — the preview never joins another surface's fetch; if it
   ever shares the detail dialog's cache, each awaiter decouples via `WaitAsync(ct)`.
4. **Debounced movement.** Cursor moves repaint tier 1 instantly; at most one tier-2
   fetch is in flight, keyed to where the cursor stopped.

Every rule is pure-VM behaviour → plain unit tests (fake store with controllable
latency: move A→B→C fast; only C publishes; A/B cancelled; no torn state). The
invariants are typed, not conventional: `SingleFlightCache<K,V>` owning its one token
and a `Published<T>` atomic pair land as foundation work before the workspace builds
on them.

### The reuse boundary — no second formatter

- The detail *text composition* currently embedded in `PrDetailDialog` /
  `WorkItemDetailDialog` is **extracted into pure formatters** (`PrDetailFormatter`,
  `WorkItemDetailFormatter`) that both dialog and preview call. This extraction is the
  load-bearing refactor and lands first (behaviour unchanged, snapshot tests); the
  preview is then a second consumer. If the preview grew its own rendering, dialog and
  preview would drift — the failure ADR 0022 forbids.
- The preview projection is a **subset, not a clone**: the formatter takes a `width`
  and a `tier` (summary vs full) — the same function truncated, never a parallel
  implementation.
- **Every action stays modal**: vote/comment/state/assign, thread view, and the whole
  `DiffReviewDialog` (it keeps its own single-flight machinery and screen modes —
  ADR 0008/0017/0018). The preview shows diff *stats* at most; no diff content in
  stage B. The preview pane itself is read-only: scroll, nothing else.
- Detail view-models are **instantiated per previewed item** (cheap, id-bound state;
  `new PrDetailViewModel(store, id)` exactly as the modal path does today); the
  preview holds at most one live instance, and `Enter` still constructs a fresh VM for
  the modal. No shared mutable VM between pane and dialog in v1 — a candidate
  optimization *after* UAT, not before.

PRs ship first, work items second, sharing the machinery (ADR 0022).

## Consequences

- Discoverability by default: the second pane is visible without a keypress, at the
  cost of a config knob (`preview`) and two visual states (tier 1/tier 2) to design.
- The `Tab` break is real churn for sub-tab-via-Tab users; `[`/`]` and ADR 0023 remaps
  absorb it. The override is workspace-scoped — `Tab` elsewhere is untouched.
- The load invariant makes the #11 bug class unrepresentable rather than re-fixed; the
  cost is the typed foundations landing before any visible feature.
- The formatter extraction briefly touches the detail dialogs with zero visible change
  — accepted as the price of "no second formatter".
- Headless tests carry the invariants; the prototype and the both-driver UAT gate
  carry the feel. Neither substitutes for the other.

## Amendment — 2026-07-24 (pane implementation)

"A manual toggle is persisted in `config.toml`" above means the file is the persistence, not
that cobalt writes it: `preview = auto|off` is **read at startup and switched live by
`:preview`**, and is **never written back**. The user edits `config.toml` to make a choice
permanent — exactly how `theme` behaves, and cobalt has no config writer at all. A `:preview`
switch therefore lasts for the session.

The `off` override is applied in the shell, on top of `WorkspaceLayout.Compute`, which stays a
pure function of width. Visibility = `Compute(width).ShowPreview && preview != off`.

**The vertical budget is a content cap, not a viewport cap.** The Summary tier is width-clamped
by the formatters but vertically unbounded, so `PreviewBudget.Fit` caps the pane's text and
marks the elision (`… N more`). That cap is deliberately **independent of the pane's height**:
budgeting to the visible rows would leave nothing off-screen, reducing scrolling — the pane's
one verb — to a no-op. `PreviewBudget.MaxLines` therefore sits well above any real detail,
making it a safety valve against pathological content rather than routine truncation, and
content taller than the pane is kept precisely so the pane has something to scroll through.
