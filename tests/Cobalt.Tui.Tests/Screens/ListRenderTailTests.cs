using Cobalt.Tui.Screens;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>Unit-level: the build/bind/restore tail shared by the work-item and PR list views —
/// placeholder vs formatted rows, the bound source, and the clamped selection restore.</summary>
public class ListRenderTailTests
{
    private static (ListView List, Window Window) HeadlessList()
    {
        var list = new ListView();
        var window = new Window();
        window.Add(list);
        window.Layout(new System.Drawing.Size(80, 20));
        return (list, window);
    }

    [Fact]
    public void Empty_List_With_EmptyText_Binds_The_Placeholder_Row()
    {
        var (list, window) = HeadlessList();
        using var _ = window;
        var formatterCalls = 0;

        var rendered = ListRenderTail.Apply(list, 0, "no rows, because reasons", Format, 0);

        Assert.Equal(["no rows, because reasons"], rendered);
        Assert.Equal(1, list.Source?.Count);
        Assert.Equal(0, formatterCalls);

        IReadOnlyList<string> Format()
        {
            formatterCalls++;
            return [];
        }
    }

    [Fact]
    public void NonEmpty_List_Invokes_The_Formatter_And_Binds_Its_Rows()
    {
        var (list, window) = HeadlessList();
        using var _ = window;

        var rendered = ListRenderTail.Apply(list, 3, null, () => ["a", "b", "c"], 0);

        Assert.Equal(["a", "b", "c"], rendered);
        Assert.Equal(3, list.Source?.Count);
    }

    [Fact]
    public void Restores_The_Selection_Clamped_To_The_Row_Count()
    {
        var (list, window) = HeadlessList();
        using var _ = window;

        ListRenderTail.Apply(list, 3, null, () => ["a", "b", "c"], target: 10);

        Assert.Equal(2, list.SelectedItem);
    }

    [Fact]
    public void Empty_List_Without_EmptyText_Binds_The_Formatter_Result_And_Skips_The_Restore()
    {
        var (list, window) = HeadlessList();
        using var _ = window;

        var rendered = ListRenderTail.Apply(list, 0, null, () => [], target: 0);

        Assert.Empty(rendered);
        Assert.Equal(0, list.Source?.Count);
        Assert.Null(list.SelectedItem);
    }
}
