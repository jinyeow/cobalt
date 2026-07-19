# 0023 — Keybinding remap config: `[keys.<scope>]` in config.toml

Status: Accepted · Date: 2026-07-18

## Context

ADR 0022 listed user-remappable keybindings as out of scope "until decided". ADR 0007
already designed `KeyBindingTable` as the single edit point for a future remapping
config — one seam the router, keybar, and `?` help all render from, so a remap can never
drift from behaviour. This ADR makes that decision: the config schema, how it composes
with the built-in defaults, and how conflicts are reported.

## Decision

- **Schema**: a `[keys.<scope>]` table per `KeyScope` (lowercased: `global`,
  `workitemlist`, `workitemdetail`, `pullrequestlist`, `pullrequestdetail`, `diffreview`,
  `threadview`). Each entry is `command-name = "token sequence"` (kebab-case command,
  e.g. `move-down = "n"`) or an array for multiple bindings (`move-down = ["n", "g j"]`);
  `command-name = ""` unbinds it. Kebab-case command names map to `AppCommand` by
  case-insensitive, dash-stripped match, so the config vocabulary never needs its own
  registry.
- **Composition**: a config entry **replaces** that command's default bindings in the
  named scope (not additive) — "I want `n` instead of `j`" is the common case, and `""`
  unbind would otherwise be redundant with "set to nothing". A command with no config
  entry keeps its default bindings untouched. A command bound only in config, in a scope
  it had no default binding in, **extends** the table (e.g. binding `mark-viewed` — a
  `DiffReview`-only command by default — into `WorkItemList` too).
- **Locked contract**: `KeyBindingTable.FromConfig(KeysConfig config)`, a static factory:
  start from `Default()`, overlay the per-scope overrides. `KeysConfig` is a plain,
  UI-free `Core` type (`Dictionary<string scope, Dictionary<string command, IReadOnlyList<string> sequences>>`)
  parsed by `ConfigLoader.Parse` — the `AppCommand`/`KeyScope` lookup happens in
  `Cobalt.Tui`, keeping `Cobalt.Core` free of Terminal.Gui-adjacent vocabulary (ADR 0004).
  An absent `[keys]` section parses to `KeysConfig.Empty`, and `FromConfig(KeysConfig.Empty)`
  is bindings-for-bindings identical to `Default()`.
- **Fail loud**: an unknown scope name, an unknown command name, or a conflict the
  resulting table would produce (two different commands sharing an exact sequence in the
  same scope's own bindings, or one sequence becoming an unreachable prefix of another —
  `KeyBindingTable.Validate`'s existing prefix check, extended with a duplicate-sequence
  check for the new same-scope-shadow case) all throw at startup with the offending
  scope/command/sequence named, matching `ConfigLoader`'s existing fail-loud style.
  Deliberate cross-scope shadowing (a scoped binding reusing a global key, e.g.
  `DiffReview`'s `h` over `Global`'s `h`) stays legal — `KeymapRouter` resolves it by
  scoped-bindings-enumerate-first, so it is not a conflict.
- **Zero-touch surfacing**: the bottom keybar (`KeybarFormatter`) and the `?` help
  overlay (`HelpText`) already render from the live `KeyBindingTable` (ADR 0021), so a
  remapped table shows the new key with no changes to either formatter.

## Consequences

- The remap surface is exactly one seam (`KeyBindingTable.FromConfig`); nothing else in
  the input chain (`KeyTokenizer`, `KeymapRouter`, `KeybarFormatter`, `HelpText`) changes.
- Replace-not-additive composition means a user who wants to *add* an alias alongside a
  default binding must repeat it: `move-down = ["j", "n"]`. Accepted — it keeps `""`
  unbind meaningful and matches "remap" as the primary use case.
- Config command names are validated against the current `AppCommand` enum, so a typo or
  a renamed command fails at startup instead of silently doing nothing.
- Token syntax itself (e.g. malformed chord literals) is not separately validated here —
  an unrecognized token simply never matches a keystroke via `KeyTokenizer`, the same as
  today's hard-coded table. A stricter token-syntax check is deferred until it proves
  needed.
