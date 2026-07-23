namespace Cobalt.Tui.ViewModels;

/// <summary>The list+preview workspace's pane geometry for a given width.</summary>
public readonly record struct WorkspacePanes(bool ShowPreview, int ListWidth, int PreviewWidth);

/// <summary>
/// Pure responsive-layout decision for the shell's list+preview workspace (ADR 0024):
/// from the content width, decide whether the preview pane is shown and how the width
/// splits between the list and the preview. Kept UI-free so the thresholds are
/// unit-tested without Terminal.Gui; the shell applies the result and re-applies it on
/// resize. Same shape as the diff dialog's <see cref="ResponsiveLayout"/>, deliberately
/// a separate threshold table.
/// </summary>
public static class WorkspaceLayout
{
    /// <summary>Below this content width the preview is hidden so the list spans the full row.</summary>
    private const int CollapseThreshold = 100;

    /// <summary>
    /// The list width in the 100–109 band: the PR row's fixed prefix (id + vote + age =
    /// 18 cells with gaps) plus a capped author column and a usable repo/title remainder.
    /// </summary>
    private const int NarrowBandListWidth = 40;

    /// <summary>At or above this width the list takes a clamped fraction instead of the fixed minimum.</summary>
    private const int WideThreshold = 110;
    private const double ListFraction = 0.45;
    private const int MinListWidth = 40;
    private const int MaxListWidth = 70;

    public static WorkspacePanes Compute(int width)
    {
        if (width < CollapseThreshold)
        {
            // No preview: the list spans the full width — exactly the pre-workspace UX.
            return new WorkspacePanes(ShowPreview: false, ListWidth: width, PreviewWidth: 0);
        }

        if (width < WideThreshold)
        {
            return new WorkspacePanes(ShowPreview: true, NarrowBandListWidth, width - NarrowBandListWidth);
        }

        var listWidth = Math.Clamp((int)(width * ListFraction), MinListWidth, MaxListWidth);
        return new WorkspacePanes(ShowPreview: true, listWidth, width - listWidth);
    }
}
