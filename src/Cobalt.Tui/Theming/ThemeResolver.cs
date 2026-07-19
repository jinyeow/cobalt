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

    /// <summary>
    /// Resolves a preset assuming full truecolor support. Kept as a delegating overload so callers
    /// that predate colour degradation (and parallel work units) still compile; it is equivalent to
    /// <see cref="Resolve(ThemeChoice, OsTheme, ColorSupport)"/> with <see cref="ColorSupport.Full"/>.
    /// </summary>
    public static ThemePreset Resolve(ThemeChoice choice, OsTheme os) =>
        Resolve(choice, os, ColorSupport.Full);

    /// <summary>
    /// Resolves a <see cref="ThemeChoice"/> (+ the OS theme when following the system, + the
    /// terminal's <paramref name="color"/> depth) to a concrete <see cref="ThemePreset"/>. The base
    /// choice picks the chrome theme name and the light/dark diff family; the colour tier then
    /// degrades the diff palette: <see cref="ColorSupport.Full"/> keeps the truecolor tints,
    /// <see cref="ColorSupport.Ansi16"/> swaps to the nearest-ANSI palette, and
    /// <see cref="ColorSupport.None"/> collapses both light and dark to the sign-only
    /// <see cref="DiffPalette.Mono"/> (ADR 0019 extension). Pure and side-effect free.
    /// </summary>
    public static ThemePreset Resolve(ThemeChoice choice, OsTheme os, ColorSupport color)
    {
        var (baseName, isLight) = choice switch
        {
            ThemeChoice.Dark => ("Default", false),
            ThemeChoice.Light => ("Light", true),
            // System follows the OS; anything but a known light signal falls back to dark.
            ThemeChoice.System => os == OsTheme.Light ? ("Light", true) : ("Default", false),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "unknown theme choice"),
        };

        var diff = color switch
        {
            ColorSupport.Full => isLight ? DiffPalette.Light : DiffPalette.Dark,
            ColorSupport.Ansi16 => isLight ? DiffPalette.Light16 : DiffPalette.Dark16,
            ColorSupport.None => DiffPalette.Mono,
            _ => throw new ArgumentOutOfRangeException(nameof(color), color, "unknown colour support"),
        };

        return new ThemePreset(baseName, diff);
    }
}
