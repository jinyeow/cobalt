namespace Cobalt.Tui.ViewModels;

/// <summary>The diff-review dialog's pane geometry for a given width.</summary>
public readonly record struct DiffLayout(bool ShowFileList, int FileListWidth, bool AllowSideBySide);

/// <summary>
/// Pure responsive-layout decision for the diff-review dialog (Item 2, ADR 0004): from
/// the dialog's content width, decide whether the changed-file list is shown (and its
/// width) and whether the diff pane has room for a two-column side-by-side view. Kept
/// UI-free so the thresholds are unit-tested without Terminal.Gui; the dialog applies the
/// result and re-applies it on resize.
/// </summary>
public static class ResponsiveLayout
{
    /// <summary>Below this content width the file list is hidden so the diff gets the whole row.</summary>
    private const int HideFileListBelow = 60;
    private const int MinFileListWidth = 20;
    private const int MaxFileListWidth = 40;
    private const double FileListFraction = 0.28;
    private const int PaneGap = 1;

    /// <summary>The diff pane needs at least this width to fit two readable columns.</summary>
    private const int MinSideBySideDiffWidth = 64;

    public static DiffLayout Compute(int totalWidth)
    {
        if (totalWidth < HideFileListBelow)
        {
            // No file list: the diff pane spans the full width.
            return new DiffLayout(ShowFileList: false, FileListWidth: 0, AllowSideBySide: totalWidth >= MinSideBySideDiffWidth);
        }

        var fileListWidth = Math.Clamp((int)(totalWidth * FileListFraction), MinFileListWidth, MaxFileListWidth);
        var diffWidth = totalWidth - fileListWidth - PaneGap;
        return new DiffLayout(ShowFileList: true, fileListWidth, AllowSideBySide: diffWidth >= MinSideBySideDiffWidth);
    }
}
