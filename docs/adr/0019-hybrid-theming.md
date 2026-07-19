# 0019 — Hybrid theming: Terminal.Gui themes for chrome, a cobalt palette for the diff

Status: Accepted · Date: 2026-07-08

## Context

Users wanted cobalt's colours to be themeable, with dark + light presets and an option to
follow the OS light/dark setting. Two surfaces need colour: the **app chrome + syntax token
foregrounds** (already driven by Terminal.Gui's `VisualRole.Code*` scheme roles), and the
**diff tints** (added/removed/emphasis backgrounds + gutter signs), which were hard-coded RGB
in `DiffListDataSource` because they live *outside* TG's scheme roles (ADR 0010).

## Decision

- **Hybrid.** Let Terminal.Gui theme the whole app + syntax roles via its own
  `Configuration.ConfigurationManager`; keep the diff tints cobalt-owned as a `DiffPalette`
  resolved per theme. TG's `Scheme` has a **fixed** set of `VisualRole`s (decompiled), so the
  diff tints can't be promoted into theme roles without abusing role semantics — a `DiffPalette`
  is the honest model. `DiffListDataSource` reads the ambient `ThemeService.CurrentPalette` on
  each render, so a switch recolours the diff live without threading a palette through every
  construction site (the theme is global state, so an ambient accessor is correct).

- **Presets & selection.** `theme = "dark" | "light" | "system"` in `config.toml` (default
  `dark`); a `:theme dark|light|system` palette command switches live. `dark` maps to TG's
  built-in **`"Default"`** theme (== the pre-theming look, so an empty config is unchanged);
  `light` maps to TG's `"Light"`. `ThemeResolver` (pure, unit-tested) turns a `ThemeChoice`
  (+ the OS theme when following the system) into a `ThemePreset(TgThemeName, DiffPalette)`.

- **Scope the config surface.** `ConfigurationManager.Enable(ConfigLocations.LibraryResources)`
  loads **only** the themes embedded in `Terminal.Gui.dll` (merged onto TG's hard-coded
  defaults) — never `~/.tui`, `./.tui`, `TUI_CONFIG`, or app resources — so a user's unrelated
  TG config can't shift cobalt's look, and cobalt owns the theme set. The library config's
  non-theme settings (`Key.Separator`, `PopoverMenu.DefaultKey`, `Force16Colors`, mouse) were
  checked to equal TG's hard-coded defaults, so enabling it changes no keybindings/behaviour.

- **System-follow.** `IOsThemeMonitor` abstracts OS detection: `WindowsOsThemeMonitor` reads
  `HKCU\…\Personalize\AppsUseLightTheme` and watches it via `RegNotifyChangeKeyValue`, raising
  `Changed`; other platforms get a no-op that reports `Unknown` (best-effort, follow-up). When
  `theme = "system"`, a change re-resolves and re-applies on the UI thread.

- **Obsolete-API containment.** In TG 2.4.16 `ConfigurationManager` is `[Obsolete]` (CS0618) —
  yet it is the *only* runtime-theming API; its Modular-Extension-Configuration replacement
  can't own theme data yet (TG #5416). The suppression is confined to `ThemeService.cs`, the
  single seam onto TG theming, scoped to the two calls that touch it.

## Consequences

- Switching a theme (`:theme`, or an OS change under `system`) sets `ThemeManager.Theme`,
  `ConfigurationManager.Apply()`, records the `DiffPalette`, and repaints via
  `app.LayoutAndDraw(true)`; views re-resolve `SchemeName` live, so nothing is recreated.
- The pure parts (config parse, `ThemeResolver`, `DiffPalette`, the OS-detect mapping) are
  unit-tested; the rendered colours themselves are PTY/manual (ADR 0010). A manual check must
  confirm `theme = dark` is byte-identical to the pre-theming look, and the live switch + OS
  follow in a real terminal.
- When TG's MEC configuration can own theme data (TG #5416), `ThemeService` is the one file to
  migrate off the obsolete API.

## Extension (2026-07): colour-degradation tiers

The original decision assumed a truecolor terminal — the diff tints are 24-bit RGB. Terminals
that lack truecolor (or opt out via `NO_COLOR`) rendered those RGB values against the nearest
colour the driver could manage, which is not something cobalt controlled. This extension makes
the degradation explicit and cobalt-owned, still entirely through the `DiffPalette`/`ThemeService`
seam above — no new hard-coded colours, no renderer rewrite.

- **Capability detection is pure.** `TerminalCapabilities.Detect(Func<string, string?> getEnv)`
  decides a `ColorSupport` tier (`Full` truecolor / `Ansi16` / `None` monochrome) and a
  `UnicodeSafe` flag **deterministically from environment variables** — never by probing the live
  terminal — reusing the same injected env seam as `CobaltTuiApp.ResolveDriver` (ADR 0016), so it
  is fully unit-testable. Precedence: a non-empty `NO_COLOR` → `None`; an explicit `COBALT_COLOR`
  override (`none`/`16`/`true`); `TERM=dumb` → `None`; `COLORTERM=truecolor|24bit`, `WT_SESSION`,
  or a `TERM` naming `truecolor|24bit|256color` → `Full`; otherwise `Ansi16`. `UnicodeSafe` is
  `false` for the Linux console and a dumb terminal (detected and exposed now, consumed by a later
  renderer change).

- **The tier degrades the diff palette, not the chrome model.** `ThemeResolver.Resolve` gains a
  three-argument form `(ThemeChoice, OsTheme, ColorSupport)`: the choice still picks the chrome
  theme name and the light/dark diff family, then the tier swaps the `DiffPalette` — `Full` keeps
  the truecolor tints (byte-identical to before this extension), `Ansi16` uses the nearest
  `ColorName16` palettes (`DiffPalette.Dark16`/`Light16`), and `None` collapses **both** light and
  dark to `DiffPalette.Mono`. Mono carries no background tint (add and remove share one
  background); the renderer's `+`/`-` sign gutters and attribute emphasis carry the meaning, so a
  monochrome diff stays legible. A two-argument overload delegating to `Full` is retained so
  pre-degradation callers still compile.

- **Detected capabilities are ambient, like the palette.** `ThemeService.Capabilities` is published
  once at startup (`ThemeService.SetCapabilities`, called before `Enable()`), defaulting to
  `Full` + Unicode-safe so the pre-detection look is unchanged. Startup also sets Terminal.Gui's
  `Force16Colors` when `Capabilities.Color < ColorSupport.Full`, so the chrome degrades in step
  with the diff — this is the library-config setting this ADR already audited as equal to TG's
  hard-coded default, now set deliberately.

- **Testing.** As above, the rendered colours are PTY/manual, but detection, resolution, and the
  palette tiers are pure and unit-tested: `Detect` over an env permutation table, `Resolve` across
  the `(choice × os × support)` matrix, `Mono` has no add/remove tint, and the 16-tier palettes are
  asserted to contain only `ColorName16`-representable colours.
