using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The pure responsive-layout decision for the diff-review dialog (Item 2, ADR 0004):
/// given the dialog's content width, decide whether the file list shows (and how wide)
/// and whether the diff pane is wide enough for a side-by-side view. No Terminal.Gui.
/// </summary>
public class ResponsiveLayoutTests
{
    [Fact]
    public void Narrow_Hides_The_File_List_And_Forbids_Side_By_Side()
    {
        var layout = ResponsiveLayout.Compute(50);

        Assert.False(layout.ShowFileList);
        Assert.Equal(0, layout.FileListWidth);
        Assert.False(layout.AllowSideBySide);
    }

    [Fact]
    public void Medium_Shows_The_File_List_But_Forces_Unified()
    {
        var layout = ResponsiveLayout.Compute(72);

        Assert.True(layout.ShowFileList);
        Assert.InRange(layout.FileListWidth, 20, 40);
        Assert.False(layout.AllowSideBySide); // diff pane too narrow for two columns
    }

    [Fact]
    public void Wide_Shows_The_File_List_And_Allows_Side_By_Side()
    {
        var layout = ResponsiveLayout.Compute(120);

        Assert.True(layout.ShowFileList);
        Assert.InRange(layout.FileListWidth, 20, 40);
        Assert.True(layout.AllowSideBySide);
    }

    [Fact]
    public void Very_Wide_Caps_The_File_List_Width()
    {
        var layout = ResponsiveLayout.Compute(220);

        Assert.Equal(40, layout.FileListWidth); // capped so the diff keeps the room
        Assert.True(layout.AllowSideBySide);
    }
}
