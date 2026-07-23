namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Depth of a detail rendering (ADR 0024): <see cref="Full"/> is the modal dialog's
/// complete body; <see cref="Summary"/> is the same composition truncated for the
/// list-preview pane — never a parallel implementation.
/// </summary>
public enum PreviewTier
{
    Summary,
    Full,
}
