using Cobalt.Core.Config;

namespace Cobalt.Tui.Theming;

/// <summary>
/// Resolves a <see cref="ThemeChoice"/> (+ the OS theme when following the system) to a
/// concrete <see cref="ThemePreset"/>. Pure and side-effect free, so it's driven directly
/// from config at startup and from <c>:theme</c> at runtime.
/// </summary>
public static class ThemeResolver
{
    private static readonly ThemePreset DarkPreset = new("Default", DiffPalette.Dark);
    private static readonly ThemePreset LightPreset = new("Light", DiffPalette.Light);

    public static ThemePreset Resolve(ThemeChoice choice, OsTheme os) => choice switch
    {
        ThemeChoice.Dark => DarkPreset,
        ThemeChoice.Light => LightPreset,
        // System follows the OS; anything but a known light signal falls back to dark.
        ThemeChoice.System => os == OsTheme.Light ? LightPreset : DarkPreset,
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "unknown theme choice"),
    };
}
