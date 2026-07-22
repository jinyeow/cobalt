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
  a bare `command-name = ""` unbinds it (an empty or whitespace-only entry *inside* an
  array is rejected instead — that's a typo, not the unbind syntax). Kebab-case command
  names map to `AppCommand` by an **exact** kebab-case match, case-insensitive
  (`move-down` only, not a hyphen-stripped `movedown` or a malformed `move--down`), so the
  config vocabulary never needs its own registry. Two config keys in the same scope that
  resolve to the same command (e.g. `refresh` and `Refresh` — distinct TOML keys, same
  `AppCommand` case-insensitively) is a config error, not last-wins.
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
- **Fail loud**: an unknown scope name, an unknown/duplicate-resolved command name, a
  duplicate sequence for the same command, or a conflict the resulting table would
  produce (two different commands sharing an exact sequence in the same scope's own
  bindings, or one sequence becoming an unreachable prefix of another —
  `KeyBindingTable.Validate`'s existing prefix check, extended with a duplicate-sequence
  check for the new same-scope-shadow case) all throw at startup with the offending
  scope/command/sequence named, matching `ConfigLoader`'s existing fail-loud style.
  Deliberate cross-scope shadowing (a scoped binding reusing a global key, e.g.
  `DiffReview`'s `h` over `Global`'s `h`) stays legal — `KeymapRouter` resolves it by
  scoped-bindings-enumerate-first, so it is not a conflict. A sequence `KeymapRouter`
  could never deliver — containing `"Esc"` (always short-circuits to cancel), starting
  with a bare digit `0`-`9` (always consumed as a count prefix), or containing a token
  `KeyTokenizer` can never emit (amended 2026-07-22 after UAT: `"5j"` is one dead
  two-character token, not the two keys `"5 j"`; valid tokens are a single rune, a
  `C-a`..`C-z` chord, or Enter/Tab/S-Tab/Up/Down) — is rejected at `FromConfig` time
  rather than silently installed as a dead binding. A duplicate `[keys.<scope>]` table is
  rejected whether it differs by TOML key case (`[keys.global]` + `[keys.Global]`,
  `ConfigLoader`'s case check) or repeats exactly (`[keys.global]` twice — invalid TOML
  that the deserializer alone silently resolves first-wins; `ConfigLoader` now runs the
  parser's semantic validation pass and fails loud on its diagnostics — amended
  2026-07-22 after UAT caught the second table being dropped without a signal).
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
- Token syntax IS validated (the deferral above expired 2026-07-22): UAT showed
  `move-down = "5j"` silently replacing the default with a dead binding — losing `j`
  entirely — so `FromConfig` now rejects any token `KeyTokenizer` cannot emit.
- A scope's own default still shadows a global remap inside that scope — the same
  legal scoped-over-global precedence as ever: with `[keys.global] move-down = ["j",
  "n"]`, `n` stays search-next inside diff review. Remap `[keys.diffreview]` too when
  the alias should follow into a scope that already binds the key.
- A config binding that legally shadows a *default* global binding it didn't ask to
  shadow (e.g. remapping a `WorkItemList` command onto a key some other default global
  binding already uses) is not warned about — it's the same legal scoped-over-global
  mechanism `DiffReview`'s defaults already rely on, and a warning surface needs shell
  wiring the shell owner (not this ADR) provides. Deferred.
