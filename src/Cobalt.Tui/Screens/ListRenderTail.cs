using System.Collections.ObjectModel;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>The build/bind/restore tail the work-item and PR list views share.</summary>
internal static class ListRenderTail
{
    /// <summary>
    /// Builds the rendered row texts — the empty-state placeholder when the list is empty and
    /// guidance exists (Helpful-empty-states item 3: explain why the list is empty instead of a
    /// blank body), otherwise <paramref name="formatRows"/>'s output — binds them to
    /// <paramref name="list"/>, and restores the clamped selection.
    /// SetSource nulls SelectedItem in 2.4.16, so the caller captures <paramref name="target"/>
    /// before calling and this restores it — otherwise a background reload snaps the highlight
    /// back to the top. The list is the source of truth for that capture.
    /// </summary>
    internal static IReadOnlyList<string> Apply(
        ListView list,
        int rowCount,
        string? emptyStateText,
        Func<IReadOnlyList<string>> formatRows,
        int target)
    {
        IReadOnlyList<string> rendered = rowCount == 0 && emptyStateText is { } emptyText
            ? [emptyText]
            : formatRows();
        list.SetSource(new ObservableCollection<string>(rendered));
        if (rowCount > 0)
        {
            list.SelectedItem = Math.Clamp(target, 0, rowCount - 1);
        }
        return rendered;
    }
}
