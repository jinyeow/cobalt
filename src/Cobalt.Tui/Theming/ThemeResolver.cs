using Cobalt.Core.Config;
using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Theming;

/// <summary>
/// Resolves a <see cref="ThemeChoice"/> (+ the OS theme when following the system) to a
/// concrete <see cref="ThemePreset"/>. Pure and side-effect free, so it's driven directly
/// from config at startup and from <c>:theme</c> at runtime.
/// </summary>
public static class ThemeResolver
{
    /// <summary>The light preset's diff palette — readable on a light background.</summary>
    private static DiffPalette LightDiffPalette { get; } = new(
        AddedBackground: new Color("#d6f5d6"),
        AddedEmphasisBackground: new Color("#a6e0a6"),
        RemovedBackground: new Color("#f8d6d6"),
        RemovedEmphasisBackground: new Color("#e8a6a6"),
        AddedGutterForeground: new Color("#1e6b1e"),
        RemovedGutterForeground: new Color("#8a1f1f"),
        SearchHitBackground: new Color("#fff2a8"));

    private static readonly ThemePreset DarkPreset = new("Default", DiffPalette.Dark);
    private static readonly ThemePreset LightPreset = new("Light", LightDiffPalette);

    public static ThemePreset Resolve(ThemeChoice choice, OsTheme os) => choice switch
    {
        ThemeChoice.Light => LightPreset,
        ThemeChoice.System => os == OsTheme.Light ? LightPreset : DarkPreset,
        _ => DarkPreset,
    };
}
