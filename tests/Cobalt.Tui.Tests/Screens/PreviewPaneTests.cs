using System.Drawing;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.ViewModels;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The read-only preview pane (#48, ADR 0024): it renders the shared detail formatters'
/// Summary tier — never its own composition — caps the result to its vertical budget, and
/// scrolls through the shared VimScroll seam. Headless view-level: no driver, no Init().
/// </summary>
public class PreviewPaneTests
{
    [Fact]
    public async Task Renders_The_Pr_Detail_Formatters_Summary_Output()
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(TestContext.Current.CancellationToken);
        var expected = PrDetailFormatter.Render(vm, 60, PreviewTier.Summary);
        using var pane = new PreviewPane();

        pane.SetContent(expected);

        // TextView hands text back with the platform's newline; compare on '\n' like the
        // detail-dialog render snapshots do.
        Assert.Equal(expected, pane.Body.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Renders_The_Work_Item_Formatters_Summary_Output()
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(TestContext.Current.CancellationToken);
        var expected = WorkItemDetailFormatter.Render(vm, 60, PreviewTier.Summary);
        using var pane = new PreviewPane();

        pane.SetContent(expected);

        Assert.Equal(expected, pane.Body.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Formatter_Output_Is_Shown_Whole()
    {
        // The Summary tier is width-clamped but vertically unbounded; the cap is a safety valve
        // set well above any real detail, so ordinary formatter output is never truncated.
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(TestContext.Current.CancellationToken);
        var summary = PrDetailFormatter.Render(vm, 60, PreviewTier.Summary);
        using var pane = new PreviewPane();

        pane.SetContent(summary);

        Assert.Equal(summary, pane.Body.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Content_Beyond_The_Hard_Cap_Ends_In_The_Omission_Marker()
    {
        // The cap bounds what the pane holds regardless of its height, so pathological detail
        // is elided visibly rather than silently.
        var overflowing = PreviewBudget.MaxLines + 100;
        using var pane = new PreviewPane();

        pane.SetContent(string.Join("\n", Enumerable.Range(0, overflowing).Select(i => $"line {i}")));

        var shown = pane.Body.Text.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(PreviewBudget.MaxLines, shown.Length);
        Assert.Equal($"… 101 more", shown[^1]);
    }

    [Fact]
    public void With_No_Content_It_Shows_The_Empty_State()
    {
        // #48 never sets content (the source is #49), so the shipped pane must say so
        // rather than render as a blank hole beside the list.
        using var pane = new PreviewPane();

        Assert.Equal("(no preview)", pane.Body.Text);
    }

    [Fact]
    public void A_Pane_Shorter_Than_Its_Content_Keeps_Every_Line_So_It_Can_Scroll()
    {
        // The cap is deliberately NOT the pane's height: truncating to the visible rows would
        // leave nothing off-screen, making the pane's one verb — scroll — a no-op.
        var pane = new PreviewPane { Width = Dim.Fill(), Height = Dim.Fill() };
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        window.Add(pane);
        window.Layout(new Size(40, 5));

        pane.SetContent(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line {i}")));

        Assert.Equal(40, pane.Body.Text.ReplaceLineEndings("\n").Split('\n').Length);
        Assert.True(pane.Viewport.Height < 40, "the pane must be shorter than its content to prove anything");
    }

    /// <summary>A laid-out pane holding more lines than it can show, so a scroll has somewhere to
    /// go. Nothing is overridden — this is exactly the production path.</summary>
    private static PreviewPane LaidOutPane(int lines = 200, int width = 40, int height = 12)
    {
        var pane = new PreviewPane { Width = Dim.Fill(), Height = Dim.Fill() };
        var window = new Window();
        window.Add(pane);
        window.Layout(new Size(width, height));
        pane.SetContent(string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i}")));
        return pane;
    }

    [Fact]
    public void J_And_K_Scroll_The_Pane_By_A_Line()
    {
        using var pane = LaidOutPane();

        pane.Scroll(AppCommand.MoveDown, null);
        Assert.Equal(1, pane.Body.CurrentRow);

        pane.Scroll(AppCommand.MoveDown, 5); // count prefix
        Assert.Equal(6, pane.Body.CurrentRow);

        pane.Scroll(AppCommand.MoveUp, null);
        Assert.Equal(5, pane.Body.CurrentRow);
    }

    [Fact]
    public void Ctrl_D_And_Ctrl_U_Scroll_The_Pane_By_A_Half_Page()
    {
        using var pane = LaidOutPane();
        var half = Math.Max(1, pane.Body.Viewport.Height / 2);

        pane.Scroll(AppCommand.HalfPageDown, null);
        Assert.Equal(half, pane.Body.CurrentRow);

        pane.Scroll(AppCommand.HalfPageUp, null);
        Assert.Equal(0, pane.Body.CurrentRow);
    }

    [Fact]
    public void Gg_And_G_Jump_To_The_Top_And_Bottom()
    {
        using var pane = LaidOutPane();

        pane.Scroll(AppCommand.MoveBottom, null);
        var bottom = pane.Body.CurrentRow;
        Assert.True(bottom > 0, "G should scroll away from the top");

        pane.Scroll(AppCommand.MoveTop, null);
        Assert.Equal(0, pane.Body.CurrentRow);
    }
}
