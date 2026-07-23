using Cobalt.Tui.Input;

namespace Cobalt.Tui.ViewModels;

/// <summary>The two panes of the list+preview workspace (ADR 0024).</summary>
public enum WorkspacePane
{
    List,
    Preview,
}

/// <summary>Where the workspace routes a matched movement command.</summary>
public enum WorkspaceKeyRoute
{
    ListCursor,
    PreviewScroll,
}

/// <summary>
/// Pane focus + key routing for the list+preview workspace (ADR 0024 fork C). Focus is
/// workspace state owned here — never inferred from Terminal.Gui focus — so every
/// routing decision is headless-testable; the shell maps <see cref="FocusedPane"/> to a
/// Terminal.Gui SetFocus in exactly one place. UI-free.
/// </summary>
public sealed class WorkspaceViewModel
{
    /// <summary>The pane that owns movement keys. Invariant: <see cref="WorkspacePane.Preview"/>
    /// only while <see cref="PreviewVisible"/> — collapsing the preview forces focus back to the list.</summary>
    public WorkspacePane FocusedPane { get; private set; } = WorkspacePane.List;

    /// <summary>Whether the preview pane is shown (the layout's collapse decision, ADR 0024).</summary>
    public bool PreviewVisible { get; private set; }

    /// <summary>The layout's show/collapse seam (#48 calls this from the workspace layout).
    /// Collapsing enforces the invariant: focus can never stay on a hidden preview.</summary>
    public void SetPreviewVisible(bool visible)
    {
        PreviewVisible = visible;
        if (!visible)
        {
            FocusedPane = WorkspacePane.List;
        }
    }

    /// <summary>Move focus toward the list (<c>C-h</c>). Returns true iff <see cref="FocusedPane"/> changed.</summary>
    public bool FocusLeft()
    {
        if (FocusedPane == WorkspacePane.List)
        {
            return false;
        }
        FocusedPane = WorkspacePane.List;
        return true;
    }

    /// <summary>Move focus toward the preview (<c>C-l</c>); a no-op while the preview is hidden.
    /// Returns true iff <see cref="FocusedPane"/> changed.</summary>
    public bool FocusRight()
    {
        if (!PreviewVisible || FocusedPane == WorkspacePane.Preview)
        {
            return false;
        }
        FocusedPane = WorkspacePane.Preview;
        return true;
    }

    /// <summary>
    /// The workspace-Tab decision (ADR 0024: Tab claims pane-focus cycling inside the
    /// workspace). While the preview is visible, toggles <see cref="FocusedPane"/> and
    /// returns true (consumed); while hidden, returns false and the caller falls back to
    /// today's Tab semantics — below the collapse threshold the UX stays exactly today's.
    /// </summary>
    public bool CyclePane()
    {
        if (!PreviewVisible)
        {
            return false;
        }
        FocusedPane = FocusedPane == WorkspacePane.List ? WorkspacePane.Preview : WorkspacePane.List;
        return true;
    }

    /// <summary>
    /// Where a matched movement command (j/k/C-d/C-u…) goes: preview scroll iff the
    /// preview pane is focused, the list cursor otherwise. Takes the command so #48 can
    /// refine the decision per command without a signature change; the M5 rule is purely
    /// the focused pane.
    /// </summary>
    public WorkspaceKeyRoute Route(AppCommand command) =>
        FocusedPane == WorkspacePane.Preview ? WorkspaceKeyRoute.PreviewScroll : WorkspaceKeyRoute.ListCursor;
}
