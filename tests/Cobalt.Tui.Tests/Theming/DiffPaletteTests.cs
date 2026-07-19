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

    [Fact]
    public void Mono_Preset_Has_No_Diff_Tint_So_Sign_Gutters_Carry_The_Meaning()
    {
        var mono = DiffPalette.Mono;

        // No colour tint distinguishes add from remove: every diff background is identical, so the
        // renderer's +/- sign gutters and attribute emphasis are what read (ADR 0019 extension).
        Assert.Equal(mono.AddedBackground, mono.RemovedBackground);
        Assert.Equal(mono.AddedBackground, mono.AddedEmphasisBackground);
        Assert.Equal(mono.AddedBackground, mono.RemovedEmphasisBackground);
        // The gutter colour cannot carry meaning either — the sign glyph alone does.
        Assert.Equal(mono.AddedGutterForeground, mono.RemovedGutterForeground);
    }

    [Theory]
    [MemberData(nameof(SixteenColorPresets))]
    public void Sixteen_Color_Presets_Use_Only_ColorName16_Representable_Colours(DiffPalette palette)
    {
        foreach (var color in AllColors(palette))
        {
            // A colour is 16-representable iff it round-trips through its nearest ColorName16.
            Assert.Equal(color, new Color(color.GetClosestNamedColor16()));
        }
    }

    public static TheoryData<DiffPalette> SixteenColorPresets() =>
        new() { DiffPalette.Dark16, DiffPalette.Light16, DiffPalette.Mono };

    private static IEnumerable<Color> AllColors(DiffPalette p) =>
    [
        p.AddedBackground, p.AddedEmphasisBackground, p.RemovedBackground, p.RemovedEmphasisBackground,
        p.AddedGutterForeground, p.RemovedGutterForeground, p.SearchHitBackground,
    ];
}
