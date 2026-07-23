using System.Drawing;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// Pins the detail dialogs' rendered body byte-for-byte (goldens captured from the
/// pre-extraction RenderBody), so the formatter extraction (ADR 0024 "no second
/// formatter") provably changes nothing. The body text is width-independent — the
/// TextView word-wraps — so the same golden holds at every dialog size; asserting at
/// two sizes pins that too. TextView.Text round-trips with platform line endings,
/// hence the normalization to '\n'.
/// </summary>
public class DetailDialogRenderSnapshotTests
{
    private static readonly IApplication App = Application.Create();

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 24)]
    public async Task PrDialog_Body_Matches_The_Golden(int width, int height)
    {
        var vm = await DetailFormatterFixture.LoadedPrVmAsync(TestContext.Current.CancellationToken);
        var detail = new PrDetailDialog(App, vm, null!, _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(width, height));

        Assert.Equal(DetailFormatterFixture.PrFullGolden, detail.Body.Text.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 24)]
    public async Task WorkItemDialog_Body_Matches_The_Golden(int width, int height)
    {
        var vm = await DetailFormatterFixture.LoadedWorkItemVmAsync(TestContext.Current.CancellationToken);
        var detail = new WorkItemDetailDialog(App, vm, null!, _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(width, height));

        Assert.Equal(DetailFormatterFixture.WorkItemFullGolden, detail.Body.Text.ReplaceLineEndings("\n"));
    }
}
