namespace Cobalt.Tui.Theming;

/// <summary>
/// A resolved theme: the Terminal.Gui theme name that colours the app chrome + syntax roles
/// (e.g. <c>"Default"</c> for dark, <c>"Light"</c>), paired with the cobalt-owned
/// <see cref="DiffPalette"/> for the diff pane. Produced by <c>ThemeResolver</c> from a
/// <see cref="Core.Config.ThemeChoice"/> (+ the OS theme when following the system).
/// </summary>
public sealed record ThemePreset(string TgThemeName, DiffPalette Diff);
