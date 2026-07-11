namespace Cobalt.Tui.Theming;

/// <summary>The operating system's light/dark preference, as far as cobalt can detect it.</summary>
public enum OsTheme
{
    /// <summary>Could not be determined (unsupported platform, or the setting is absent).</summary>
    Unknown,

    Dark,

    Light,
}
