namespace Cobalt.Core.Config;

/// <summary>
/// The colour theme selection from <c>config.toml</c> (<c>theme = "dark"|"light"|"system"</c>).
/// <see cref="System"/> follows the OS light/dark setting where cobalt can detect it, falling
/// back to <see cref="Dark"/>. The product default is <see cref="Dark"/> (today's look).
/// </summary>
public enum ThemeChoice
{
    /// <summary>The dark preset — cobalt's original colours.</summary>
    Dark,

    /// <summary>The light preset.</summary>
    Light,

    /// <summary>Follow the operating system's light/dark setting (best-effort per platform).</summary>
    System,
}
