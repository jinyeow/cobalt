# TUI improvement batch — implement-ready spec

Seven items, partitioned into five work units (A–E) with disjoint file ownership for
parallel TDD implementers. Units A–D run in parallel; **unit E is the single owner of the
shared shell spine (`CobaltShell.cs`, `CobaltTuiApp.cs`) and lands last**, wiring A–D
against the contracts locked in §Contracts. All new logic goes in pure functions /
view-models (ADR 0004); keys and hints derive from `KeyBindingTable` (ADR 0007, 0022).

## Items

### 1. Keybinding remap config (`[keys]` in `config.toml`)

**What:** user-defined overrides/extensions of the default binding table, applied at load.
**Owned files:** `src/Cobalt.Core/Config/CobaltConfig.cs`, `ConfigLoader.cs` (+ new
`KeysConfig.cs`); `src/Cobalt.Tui/Input/KeyBindingTable.cs`;
`tests/Cobalt.Core.Tests/Config/*`, `tests/Cobalt.Tui.Tests/Input/KeyBindingTableTests.cs`.
**Approach:** ADR 0007 designed `KeyBindingTable` as the single remap seam
(`KeyBindingTable.cs:6`, ADR 0007:16-17). Add `KeyBindingTable.FromConfig(KeysConfig)`:
start from `Default()`, apply per-scope override tables. `ConfigLoader.Parse` gains a
`[keys.<scope>]` section (scope names = `KeyScope` values, lowercased) parsed into a plain
`KeysConfig` record on `CobaltConfig` — Core stays UI-free (string→string maps; the
`AppCommand` lookup happens in `Cobalt.Tui`). Schema (decision note below):
`command-name = "token sequence"` (e.g. `move-down = "n"`, chords as `"g d"`), string
**or array** for multiple bindings; `command = ""` unbinds. Kebab-case command names map
to `AppCommand` by case-insensitive, dash-stripped match. Conflict/shadowing detection:
extend the existing `Validate()` prefix check (`KeyBindingTable.cs:145-163`) with a
duplicate-sequence-per-scope check; violations and unknown commands/scopes/tokens throw
`ConfigException` at startup with the offending line named — matching `ConfigLoader`'s
fail-loud style (`ConfigLoader.cs:95-100`). Keybar and `?` help need **zero work**: both
render from the live table (`KeybarFormatter.Render`, `KeybarFormatter.cs:56`;
`HelpText.For`, `HelpText.cs:107`) and label by `AppCommand`, so remapped keys surface
automatically.
**TDD hook:** RED unit tests — `Parse` of a `[keys.global]` table produces the expected
`KeysConfig`; `FromConfig` rebinds/unbinds/extends and `Visible()` reflects it; duplicate
and prefix conflicts throw with the sequence named; `KeybarFormatterTests`/`HelpTextTests`
case: a remapped table renders the new key.
**Acceptance:** a config with `[keys.global] move-down = "n"` makes `n` move down, shows
`n` in keybar + help; a conflicting bind fails startup with an actionable message; empty
`[keys]` is byte-identical to defaults.

### 2. `:` palette completion

**What:** Tab-complete / fuzzy-filter for palette commands and `:context`/`:project` args.
**Owned files:** `src/Cobalt.Tui/Input/PaletteCommandParser.cs` (command catalog); new
`src/Cobalt.Tui/ViewModels/PaletteSuggestionsViewModel.cs`; tests under
`tests/Cobalt.Tui.Tests/{Input,ViewModels}`. (Palette `TextField` wiring in
`CobaltShell.WirePalette` — `CobaltShell.cs:561-595` — belongs to unit E.)
**Approach:** extract the command vocabulary already hard-coded in `Parse`'s switch
(`PaletteCommandParser.cs:32-48`) into a data catalog `(name, aliases, argKind)` the parser
consumes — one source for parse + complete, mirroring the table-derives-UI rule.
`PaletteSuggestionsViewModel` is pure state: `SetInput(string)` recomputes ranked
suggestions (prefix first, then subsequence-fuzzy), `CycleNext/CyclePrev` walk them,
`Accepted` yields the completed input. Arg completion via injected providers:
`contexts: () => shellVm.ContextNames` (`ShellViewModel.cs:27`), `projects:` a
distinct-project provider from the loaded PR/WI rows. Shell (unit E) renders the current
suggestion inline in the palette row and binds Tab/S-Tab while `_palette.Visible`.
**TDD hook:** pure VM tests (input → ranked suggestions, cycle wraps, accept completes
`:con` → `:context `, `:context w` → matching names); a headless view test in the style of
`DetailDialogKeyDeliveryTests` driving `dialog.NewKeyDownEvent(Key.Tab)` on the palette
field to assert the completed text (E's integration test).
**Acceptance:** `:th<Tab>` → `:theme`; `:context <Tab>` cycles real context names;
unknown prefix leaves input unchanged; Enter still dispatches exactly as today.

### 3. Helpful empty states

**What:** every empty list explains *why* it's empty and names the next action.
**Owned files:** `src/Cobalt.Tui/ViewModels/PrListViewModel.cs`,
`WorkItemListViewModel.cs`; `src/Cobalt.Tui/Screens/PrListView.cs`,
`WorkItemListView.cs`; their test files.
**Approach:** add computed `string? EmptyStateText` to both list VMs — non-null only when
`!IsLoading && Error is null && Rows.Count == 0`. PR side keys off `ActiveTab` +
active filters, e.g. Team tab (the default, deliberately org-setup-dependent —
`PrListViewModel.cs:16-22`): "no PRs waiting on your teams — team review setup varies by
org; try ] for mine/active"; filtered: "0 of N after filter — clear with :project /
repo filter". WI side: "no work items assigned to you — :done show includes completed;
:scope org widens the query". Views render it as the list body's placeholder line where
they already branch on loading/error (`PrListView.Render`, `PrListView.cs:293-300`;
`WorkItemListView.cs:191-210`) — string comes from the VM, views only display (ADR 0004).
**TDD hook:** VM unit tests per (tab × filter × scope) case assert the exact string and
that loading/error states yield null; existing render-guard tests
(`WorkItemListRenderGuardTests`) extended to assert the placeholder reaches the view.
**Acceptance:** an empty Team tab reads "empty, not broken"; a filtered-to-zero list names
the clearing command; no string is shown while loading or on error.

### 4. 16-color / NO_COLOR degradation (extends ADR 0019)

**What:** themes degrade truecolor → ANSI-16 → monochrome instead of assuming truecolor.
**Owned files (with item 5, unit D):** `src/Cobalt.Tui/Theming/DiffPalette.cs`,
`ThemeResolver.cs`, `ThemePreset.cs`; `tests/Cobalt.Tui.Tests/Theming/*`.
**Approach:** everything already routes through the ADR 0019 seam — `DiffListDataSource`
reads ambient `ThemeService.CurrentPalette` per render (`ThemeService.cs:28`), presets are
data (`DiffPalette.cs:21-38`), resolution is pure (`ThemeResolver.cs:15-22`). Add
`DiffPalette.Dark16/Light16` (nearest `ColorName16` mappings) and `Mono` (no tint
backgrounds; gutter `+`/`-` signs and emphasis-by-attribute carry the meaning —
`SearchHitBackground` falls back to the scheme's focus role). Widen the pure resolver:
`ThemeResolver.Resolve(ThemeChoice, OsTheme, ColorSupport)` picks the preset tier; under
`Mono`, both dark and light collapse to the mono palette. No hard-coded colours anywhere
outside `DiffPalette`. TG-chrome side: startup (unit E) sets Terminal.Gui's
`Force16Colors` when `ColorSupport < TrueColor` so the chrome degrades with the diff
(the library-config setting ADR 0019 already audited, ADR 0019:33-34).
**TDD hook:** RED resolver tests per (choice × os × support) matrix; palette tests assert
Mono has `AddedBackground == RemovedBackground` (no tint) and 16-tier uses only
`ColorName16`-representable colours; existing `ThemeResolverTests` updated for the new
signature.
**Acceptance:** `NO_COLOR=1` renders a legible diff with sign-only gutters; `TERM` without
truecolor gets ANSI-16 tints; truecolor terminals are byte-identical to today.

### 5. Terminal-capability detection service

**What:** pure detection of colour depth + Unicode width support, feeding item 4.
**Owned files (unit D):** new `src/Cobalt.Tui/Theming/TerminalCapabilities.cs`; tests.
**Approach:** `record TerminalCapabilities(ColorSupport Color, bool UnicodeSafe)` with
`enum ColorSupport { None, Ansi16, Full }` (ordered so `None < Ansi16 < Full`, letting startup
couple `Force16Colors` to `Color < Full`) and a pure `Detect(Func<string, string?> env)` — same
env-func seam as `CobaltTuiApp.ResolveDriver` (`CobaltTuiApp.cs:97`, ADR 0016) so it's fully
unit-testable. Precedence: `NO_COLOR` (any non-empty value) → None; `COBALT_COLOR=none|16|true|full`
override (an unrecognised value throws, not silently ignored); `TERM=dumb` → None;
`COLORTERM=truecolor|24bit`, `WT_SESSION`, `TERM_PROGRAM` ∈ {iTerm.app, WezTerm, vscode}, or a
`TERM` containing `256color`/`truecolor`/`24bit` → Full; **otherwise (incl. a missing/unknown
`TERM`, the common Windows-conhost case) → Ansi16** — only `NO_COLOR`/`TERM=dumb` blank colour.
`UnicodeSafe` from `TERM=linux`/dumb heuristics — detected and exposed now, consumed later (no
renderer change in this batch). Published once at startup next to the palette's ambient seam:
`ThemeService.Capabilities` (set before `Enable()` in `CobaltTuiApp.Run`,
`CobaltTuiApp.cs:63` — unit E), consistent with ADR 0019's "theme is global state,
ambient accessor is correct" (`ThemeService.cs:5-13`).
**TDD hook:** table-driven `Detect` tests over env permutations (NO_COLOR beats
COLORTERM; empty-string NO_COLOR ignored per spec; override wins).
**Acceptance:** detection is deterministic per env, never probes the terminal, and item 4
consumes only its output.

### 6. `:log` operations log (ADR 0022 stage E, first slice)

**What:** in-app view of ADO operations — name, route shape, duration, outcome.
**Owned files (unit B):** `src/Cobalt.Core/Ado/AdoHttp.cs` (+ new `AdoOperation.cs`);
new `src/Cobalt.Tui/ViewModels/OperationLog.cs`;
`src/Cobalt.Tui/Input/PaletteCommandParser.cs`,
`src/Cobalt.Tui/ViewModels/ShellViewModel.cs`; tests. (Dialog display in shell — unit E.)
**Approach:** mirror `MessageLog` exactly (`MessageLog.cs:15-40`): `OperationLog` is a
capped ring of `record AdoOperation(string Name, string RouteShape, TimeSpan Duration,
int? Status, DateTimeOffset At)` with `Changed` + `TimeProvider`. Recording seam: an
optional observer callback on `AdoHttp` (all requests funnel through its four send methods,
`AdoHttp.cs:41-105`), timing each call and reporting the **route shape** — the path with
numeric IDs/GUIDs masked to `{id}` and the query trimmed to `api-version`, via a pure
`RouteShape.Of(path)` function. **Never tokens:** the observer sees only the relative
path + status; auth lives in headers the seam never touches, and the masking test guards
regression. Palette: `log` added to the catalog → `PaletteActionKind.Log` →
`ShellViewModel.LogRequested` event (same shape as `MessagesRequested`,
`ShellViewModel.cs:44,114-116`); shell shows it via `TextDialog` like `ShowMessages`
(`CobaltShell.cs:679-684`).
**TDD hook:** `OperationLog` ring/cap/Changed tests (clone of `MessageLogTests`);
`RouteShape.Of` masking table tests (`.../pullRequests/1234/threads` →
`.../pullRequests/{id}/threads`; a query string never survives beyond api-version);
parser test for `:log`; a fake-`HttpMessageHandler` test asserting one observer record
per call with sane duration/status.
**Acceptance:** `:log` opens a dialog listing recent ops newest-last with duration and
outcome; no header, token, or full query text can appear by construction.

### 7. CLAUDE.md version fix

**What:** `2.4.16` → `2.4.17` at `CLAUDE.md:110` and `CLAUDE.md:112`; the pin moved in
`Directory.Packages.props:15` when PR #8 bumped Terminal.Gui. Owned by unit A (trivial).

## Work units (disjoint ownership)

| Unit | Items | Owns (exclusive) | Depends on |
|---|---|---|---|
| **A** | 1, 7 | `Cobalt.Core/Config/*`, `Input/KeyBindingTable.cs`, `CLAUDE.md`, matching tests | — |
| **B** | 2, 6 | `Input/PaletteCommandParser.cs`, `ViewModels/{ShellViewModel,PaletteSuggestionsViewModel,OperationLog}.cs`, `Cobalt.Core/Ado/AdoHttp.cs` + `AdoOperation.cs`, matching tests | — |
| **C** | 3 | `ViewModels/{PrListViewModel,WorkItemListViewModel}.cs`, `Screens/{PrListView,WorkItemListView}.cs`, matching tests | — |
| **D** | 4, 5 | `Theming/*`, `tests/.../Theming/*` | — |
| **E** | integration | **sole owner of `App/CobaltShell.cs`, `App/CobaltTuiApp.cs`** (+ shell-level tests: `ShellChromeTests`, `KeyRoutingIntegrationTests`, new palette-key headless test) | A–D merged |

B and A both touch the `:`/key surface but never the same file (A owns the table, B the
parser). Items 2 and 6 share `PaletteCommandParser` + `ShellViewModel`, so they are one
unit by design. `KeymapRouterTests`/`KeybarFormatterTests`/`HelpTextTests` updates ride
with A (table behaviour), not E.

**Locked contracts (fixed before fan-out; E codes against these, A–D implement them):**

- `KeyBindingTable.FromConfig(KeysConfig config)` static factory; `CobaltShell` gets the
  table via its ctor instead of `KeyBindingTable.Shared` (`CobaltShell.cs:27`) — E makes
  that swap.
- `PaletteSuggestionsViewModel`: `SetInput(string)`, `CycleNext()`, `CyclePrev()`,
  `string? Current`, `string Accept()`; ctor takes
  `(Func<IReadOnlyList<string>> contexts, Func<IReadOnlyList<string>> projects)`.
- `ShellViewModel.LogRequested : event Action` + `OperationLog Operations { get; }`;
  `AdoHttp` observer: `Action<AdoOperation>?` ctor/property parameter.
- `ThemeResolver.Resolve(ThemeChoice, OsTheme, ColorSupport)` (old 2-arg overload
  removed); `TerminalCapabilities.Detect(Func<string, string?>)`;
  `ThemeService.Capabilities { get; }` set once at startup.
- C's `EmptyStateText` is a VM-only addition; its views are C-owned, so no cross-unit
  file contact.

## ADR notes owed

1. **New ADR (next number): keybinding remap config schema** — `[keys.<scope>]`,
   `command-name = "tokens"|[...]`, `""` unbinds, fail-loud conflicts. (ADR 0022 lists
   remapping as out-of-scope-until-decided; this ADR is that decision.)
2. **ADR 0019 extension: colour degradation tiers** — capability detection, the
   `ColorSupport` tier in `ThemeResolver`, Mono's sign-only diff, `Force16Colors` chrome
   coupling.
3. Housekeeping per conventions: README + CHANGELOG entries; note in ADR 0022 that stage
   E's `:log` slice shipped ahead of the rest of the stage (stage ADR completes with it).
