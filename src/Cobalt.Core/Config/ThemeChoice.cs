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

/// <summary>
/// The theme vocabulary, derived from <see cref="ThemeChoice"/> itself so a new enum member is
/// automatically accepted by <c>:theme</c> and offered by its Tab-completion — no other edits.
/// </summary>
public static class ThemeChoices
{
    /// <summary>All theme names, lowercased, in declaration order.</summary>
    public static readonly IReadOnlyList<string> Names =
        [.. Enum.GetNames<ThemeChoice>().Select(n => n.ToLowerInvariant())];
}
