using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The pure list+preview workspace geometry (ADR 0024): given the shell content width,
/// decide whether the preview pane shows and how the width splits between the list and
/// the preview. No Terminal.Gui.
/// </summary>
public class WorkspaceLayoutTests
{
    [Theory]
    [InlineData(99)]
    [InlineData(20)]
    public void Below_The_Collapse_Threshold_Hides_The_Preview_And_The_List_Takes_The_Full_Width(int width)
    {
        var panes = WorkspaceLayout.Compute(width);

        Assert.False(panes.ShowPreview);
        Assert.Equal(width, panes.ListWidth);
        Assert.Equal(0, panes.PreviewWidth);
    }

    [Theory]
    [InlineData(100, 40, 60)]
    [InlineData(109, 40, 69)]
    public void Narrow_Band_Shows_The_Preview_With_A_Fixed_Minimum_List(int width, int listWidth, int previewWidth)
    {
        var panes = WorkspaceLayout.Compute(width);

        Assert.True(panes.ShowPreview);
        Assert.Equal(listWidth, panes.ListWidth);
        Assert.Equal(previewWidth, panes.PreviewWidth);
    }

    [Theory]
    [InlineData(110, 49, 61)]  // 45% of 110, un-clamped
    [InlineData(155, 69, 86)]  // widest un-clamped 45%
    [InlineData(156, 70, 86)]  // 45% first hits the 70 cap
    [InlineData(200, 70, 130)] // capped so the preview keeps the room
    public void Wide_Band_Gives_The_List_45_Percent_Clamped(int width, int listWidth, int previewWidth)
    {
        var panes = WorkspaceLayout.Compute(width);

        Assert.True(panes.ShowPreview);
        Assert.Equal(listWidth, panes.ListWidth);
        Assert.Equal(previewWidth, panes.PreviewWidth);
    }

    [Fact]
    public void Whenever_The_Preview_Shows_The_Panes_Tile_The_Width_And_The_List_Stays_In_Its_Clamp()
    {
        for (var width = 100; width <= 300; width++)
        {
            var panes = WorkspaceLayout.Compute(width);

            Assert.True(panes.ShowPreview);
            Assert.Equal(width, panes.ListWidth + panes.PreviewWidth);
            Assert.InRange(panes.ListWidth, 40, 70);
        }
    }

    [Fact]
    public void Growing_Width_Never_Collapses_A_Shown_Preview()
    {
        var shown = false;
        for (var width = 1; width <= 300; width++)
        {
            var panes = WorkspaceLayout.Compute(width);

            Assert.False(shown && !panes.ShowPreview);
            shown = panes.ShowPreview;
        }
    }
}
