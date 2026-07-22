# Flagship design proposal — list + live-preview workspace (ADR 0022 stage B)

> **Status: DESIGN FOR HUMAN SIGN-OFF — not tickets, not implement-ready.** This stage
> changes the product's core feel (ADR 0022:66-69). A green headless suite can prove the
> invariants below but cannot prove the preview *feels* right — latency, flicker,
> focus ergonomics are judgement calls. Path to trust: sign off this design → throwaway
> prototype (`prototype` spike, not merged) → UAT in a real terminal on **both** the
> `windows` and `dotnet` drivers (ADR 0016) → then the implementing ADR + tickets.

## 1. Layout & focus

The content area (`CobaltShell._content`, `CobaltShell.cs:79`) splits into **list (left) +
preview (right)**; `Enter` still opens the full modal for actions (ADR 0022:38).

**Prereq named by ADR 0022:43-45 — generalize the diff dialog's pure layout calculator.**
`ResponsiveLayout.Compute(totalWidth) → DiffLayout` (`ResponsiveLayout.cs:25-36`) is
already the right shape: pure thresholds, unit-tested, view applies the result on resize.
Promote it to a shell-wide `WorkspaceLayout.Compute(width) → (ShowPreview, ListWidth,
PreviewWidth)` in `ViewModels/`, with the diff dialog's calculator becoming a second
consumer of the same pattern (not merged into one function — the two surfaces have
different thresholds; shared *shape*, separate tables).

Proposed geometry (prototype-tunable, these are starting values):

| Content width | Layout |
|---|---|
| ≥ 110 cols | list ~45% (clamped 40–70), preview the rest |
| 80–109 cols | list fixed-min, preview gets remainder; preview may be summary-tier only |
| < 80 cols, or preview toggled off | **list full-width, preview collapsed** — exactly today's UX |

At 80×24 the default is collapsed preview: 24 rows minus 4 chrome rows leaves too little
for a split to earn its keep; the toggle (and later stage E's `+`/`_` zoom) brings the
preview up full-content when wanted.

**Focus is an explicit workspace concept** (ADR 0022 constraint): a `WorkspaceViewModel`
owns `FocusedPane {List, Preview}`; `C-h`/`C-l` (already bound —
`AppCommand.FocusLeft/FocusRight`, `KeyBindingTable.cs:48-49`, currently informational)
drive it, and it — not Terminal.Gui focus — decides key routing: list-pane focus keeps
j/k moving the cursor; preview-pane focus routes j/k/C-d to preview scroll. The shell maps
`WorkspaceViewModel.FocusedPane` to TG `SetFocus` in one place (the `ShowSection` seam,
`CobaltShell.cs:597-633`), keeping the decision headless-testable.

## 2. THE LOAD INVARIANT (front and center)

A pane that loads on cursor-move is **exactly** the "cursor's file ≠ displayed file"
shape that produced four bugs in PR #11 and forced ADR 0008's invariants (single-flight,
atomic publish, token discipline — ADR 0008:54-77). This design adopts those rules from
day one, as spec, not as fixes:

1. **Atomic publish of `(item, detail)` as one record.** The preview state is
   `sealed record PreviewState(ItemKey Key, Detail Detail)` published in a **single
   reference write** — never two adjacent property assignments. Precedent:
   `PrDiffViewModel.DiffState` (`PrDiffViewModel.cs:81-88`), added because continuations
   run on the thread pool and concurrent selections interleave between two writes
   (ADR 0008:54-58). The renderer reads one snapshot; a torn "PR #12's title over
   PR #15's reviewers" is impossible by construction.
2. **Fetch is keyed on the item being displayed, not the moving cursor.** The load
   pipeline captures the target `ItemKey` at schedule time; on completion it publishes
   only if a monotonic sequence stamp says it is still the newest request — the
   `_loadSeq` supersede pattern already proven in both list VMs
   (`PrListViewModel.cs:94-149`, `WorkItemListViewModel.cs:87-114`). A stale completion
   is dropped whole, never partially painted.
3. **Single-flight + ADR-0008 cancellation.** One in-flight preview fetch at a time:
   scheduling a new key cancels the previous CTS (linked to the workspace-lifetime
   token), and the superseded task's fault must be observed (`IgnoreCancellationAsync`,
   as `FireAndForget`/ADR 0013 require) so it can't reach the crash-log hook. The
   ADR 0008 token trap (joiner bound to starter's token, ADR 0008:68-77) is avoided by
   construction here — the preview never *joins* another surface's fetch; if it ever
   shares the detail dialog's cache, each awaiter must decouple via `WaitAsync(ct)`.
4. **Debounced cursor movement.** Holding `j` must never enqueue N fetches. Cursor moves
   repaint the *local* tier instantly (see §4-B); the *remote* fetch is scheduled only
   after the cursor settles (~200 ms idle, injected `TimeProvider` so tests drive time).
   Result: at most one in-flight fetch, keyed to where the cursor stopped.

Every one of these is a pure-VM behaviour → plain unit tests (fake source with
controllable latency: "move A→B→C fast; only C publishes; A/B cancelled; no torn state").

## 3. THE REUSE BOUNDARY (the real integration risk)

ADR 0022:34-36 mandates reusing the existing detail view-models — no second detail
formatter. Concretely:

| Layer | Reused as-is | New (thin) | Stays modal |
|---|---|---|---|
| Data/state | `PrDetailViewModel` (`PrDetailViewModel.cs:23`), `WorkItemDetailViewModel` (`WorkItemDetailViewModel.cs:25`) — UI-free, `Changed`-event, already fetch PR+threads+policies / WI+comments+states | `PreviewViewModel` (workspace state: key, tier, `PreviewState`, seq/debounce) | — |
| Rendering | the detail *text composition* currently embedded in `PrDetailDialog`/`WorkItemDetailDialog` — **extract** it into pure formatters (`PrDetailFormatter`, `WorkItemDetailFormatter`) both dialog and preview call | `PreviewPane` view in `Screens/`: read-only, scrollable text, zero commands beyond scroll | — |
| Actions | — | — | **everything**: vote/comment/state/assign (`PrDetailDialog`, `WorkItemDetailDialog`), thread view, and the whole `DiffReviewDialog` |

- The **formatter extraction is the load-bearing refactor** and should land first as its
  own PR: dialogs re-render from the same pure formatters (behaviour unchanged, snapshot
  tests), then the preview is a second consumer. This is where integration risk lives —
  if preview grows its own rendering, dialog and preview drift (the exact failure
  ADR 0022 forbids).
- The **preview projection is a subset, not a clone**: title/state/reviewers+votes/
  policies/description head/thread summary for PRs; fields/description/latest comments
  for WIs. The formatter takes a `width` and a `tier` (summary vs full) — the same
  function, truncated, not a parallel implementation.
- **DiffReviewDialog stays fully modal.** The preview shows diff *stats* at most (already
  on the PR row); no diff content in the preview pane in stage B — the diff surface has
  its own single-flight machinery and screen-mode needs (ADR 0008, 0017/0018).
- Detail VMs are **instantiated per previewed item** (they are cheap, id-bound records of
  state — `new PrDetailViewModel(store, id)` exactly as `OpenPrDetail` does today,
  `CobaltShell.cs:479-488`); the preview holds at most one live instance. `Enter` keeps
  constructing a fresh VM for the modal — no shared mutable VM between pane and dialog in
  v1 (a candidate optimization *after* UAT, not before).

## 4. Genuine design forks (each needs a call at sign-off)

**A. Preview always-visible vs opt-in toggle.**
*Always-on (≥ threshold)* is the gh-dash/lazygit grammar and makes the feature
discoverable; *toggle-first* is safer for the 80-col + slow-org crowd and adds zero fetch
traffic until asked. — **Recommend: always-on when width allows, with a `p`-style toggle
persisted in `config.toml`** (`preview = auto|off`); collapse below threshold is automatic
either way. Rationale: stage B *is* the product bet (ADR 0022 calls it the flagship);
shipping it hidden concedes the bet, while the toggle + auto-collapse covers the hostile
cases.

**B. Preview depth: fetch-on-move vs two-tier (local instantly, remote on settle).**
Full detail on every cursor stop costs 2–3 round-trips per stop
(`PrDetailViewModel.LoadAsync` = PR + threads + policies, `PrDetailViewModel.cs:39-52`);
pure fetch-on-move feels alive but hammers the org and amplifies the §2 hazard. A
two-tier preview paints tier-1 from data the list row already holds (title, author, repo,
vote summary, comment count — zero fetches, zero latency) and upgrades to tier-2 detail
when the cursor settles. — **Recommend: two-tier.** The pane is never blank and never
stale-looking, and tier-2 traffic is bounded by the debounce. Cost: two visual states to
design (tier transition must not "flicker upward" — prototype question).

**C. Who gets `Tab` — and when the preview claims focus at all.**
`Tab` currently cycles PR sub-tabs in the PR section (alias of `[`/`]`,
`KeyBindingTable.cs:71-74`, comment already reserves it for the workspace). Claiming
`Tab` for pane-focus now matches lazygit muscle memory but breaks a shipped alias;
keeping `C-h`/`C-l` only avoids churn but hides the second pane from Tab-instinct users.
— **Recommend: claim `Tab` for pane cycling in stage B** (the reservation was made
publicly in the binding table; `[`/`]` remain the canonical sub-tab keys per ADR 0021),
and note it in the stage ADR + CHANGELOG as a deliberate break. With item 1 of the batch
(remap config), a dissenting user can rebind it — a real mitigation that didn't exist
when the reservation was written.

## 5. Process gates

- **New ADR required** (next free number): "List + live-preview workspace — concrete
  design", extending ADR 0022 stage B with the resolved forks above, the load invariant,
  and the reuse boundary; ADR 0022's stage list links to it (each stage lands with its
  own ADR — ADR 0022:18-19).
- **Throwaway prototype before tickets:** a spike branch wiring a fake
  `IPullRequestStore` with tunable latency into the split layout — answers the
  tier-transition and debounce-feel questions cheaply.
- **UAT on both drivers** (`windows` + `dotnet`, ADR 0016) in a real terminal at 80×24,
  120×30, and a resize storm, against a real org (`tools/uat` context) before the design
  is trusted; headless tests cover the invariants, never the feel.
