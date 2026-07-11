using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// Pins <see cref="DiffPalette.Dark"/> to the literal colours cobalt shipped before theming. Every
/// other theming test compares <c>DiffPalette.Dark</c> to itself (or to a preset that returns the
/// same static), which is tautological — it cannot catch an accidental edit to one of these
/// literals. This test asserts each field against a freshly-constructed expected value, so changing
/// a shipped dark-mode diff colour breaks the build (ADR 0010 / the "byte-for-byte" claim).
/// </summary>
public class DiffPaletteTests
{
    [Fact]
    public void Dark_Preset_Matches_The_Pre_Theming_Diff_Colours()
    {
        Assert.Equal(new Color("#123a12"), DiffPalette.Dark.AddedBackground);
        Assert.Equal(new Color("#1e6b1e"), DiffPalette.Dark.AddedEmphasisBackground);
        Assert.Equal(new Color("#3a1212"), DiffPalette.Dark.RemovedBackground);
        Assert.Equal(new Color("#6b2020"), DiffPalette.Dark.RemovedEmphasisBackground);
        Assert.Equal(new Color(ColorName16.BrightGreen), DiffPalette.Dark.AddedGutterForeground);
        Assert.Equal(new Color(ColorName16.BrightRed), DiffPalette.Dark.RemovedGutterForeground);
        Assert.Equal(new Color("#6b5a00"), DiffPalette.Dark.SearchHitBackground);
    }
}
