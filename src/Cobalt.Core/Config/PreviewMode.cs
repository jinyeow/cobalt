namespace Cobalt.Core.Config;

/// <summary>
/// The list+preview workspace's preview-pane setting from <c>config.toml</c>
/// (<c>preview = "auto"|"off"</c>). <see cref="Auto"/> shows the pane whenever the terminal is
/// wide enough (the responsive collapse still applies); <see cref="Off"/> keeps the list
/// full-width at every width. The product default is <see cref="Auto"/> (ADR 0024).
/// </summary>
public enum PreviewMode
{
    /// <summary>Show the preview pane when the width allows it.</summary>
    Auto,

    /// <summary>Never show the preview pane — the list spans the full width.</summary>
    Off,
}

/// <summary>
/// The preview vocabulary, derived from <see cref="PreviewMode"/> itself so a new enum member is
/// automatically accepted by <c>:preview</c> and offered by its Tab-completion — no other edits.
/// </summary>
public static class PreviewModes
{
    /// <summary>All preview-mode names, lowercased, in declaration order.</summary>
    public static readonly IReadOnlyList<string> Names =
        [.. Enum.GetNames<PreviewMode>().Select(n => n.ToLowerInvariant())];

    /// <summary>
    /// Parses an exact preview-mode name, case-insensitively. Unlike
    /// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> it rejects numeric values
    /// ("0" parses as <see cref="PreviewMode.Auto"/>) and flags-style combinations
    /// ("auto,off" parses as <see cref="PreviewMode.Off"/>) — the vocabulary is single names only.
    /// </summary>
    public static bool TryParse(string name, out PreviewMode mode)
    {
        foreach (var value in Enum.GetValues<PreviewMode>())
        {
            if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                mode = value;
                return true;
            }
        }
        mode = default;
        return false;
    }
}
