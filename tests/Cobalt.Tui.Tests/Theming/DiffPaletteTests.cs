using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// Pins the diff presets to concrete colours. The truecolor <see cref="DiffPalette.Dark"/> is the
/// pre-theming look (ADR 0010 "byte-for-byte"); the degraded tiers are pinned field-by-field so a
/// swapped add/remove background or a low-contrast gutter cannot slip through (ADR 0019 extension).
/// </summary>
public class DiffPaletteTests
{
    private static readonly Color White = new(ColorName16.White);
    private static readonly Color Black = new(ColorName16.Black);

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
    public void Mono_Preset_Paints_No_Diff_Tint_So_Rows_Inherit_The_Context_Background()
    {
        var mono = DiffPalette.Mono;

        // A null background/gutter means "inherit the terminal's normal value" — the renderer maps
        // it to the context background/foreground, so NO_COLOR carries no colour stripe at all.
        Assert.Null(mono.AddedBackground);
        Assert.Null(mono.AddedEmphasisBackground);
        Assert.Null(mono.RemovedBackground);
        Assert.Null(mono.RemovedEmphasisBackground);
        Assert.Null(mono.AddedGutterForeground);
        Assert.Null(mono.RemovedGutterForeground);
    }

    [Fact]
    public void Mono_Search_Hit_Pairs_A_Foreground_So_The_Match_Is_Not_Invisible()
    {
        var mono = DiffPalette.Mono;

        // A hit that only set a background would render foreground-on-itself (white-on-white); the
        // paired foreground makes it a legible black-on-white reverse instead.
        Assert.NotNull(mono.SearchHitForeground);
        Assert.NotEqual(mono.SearchHitBackground, mono.SearchHitForeground);
    }

    [Theory]
    [MemberData(nameof(ContrastingGutterPresets))]
    public void Sixteen_Colour_Gutters_Contrast_With_Their_Own_Background(DiffPalette palette)
    {
        // A same-hue-one-step gutter (BrightGreen on Green) is invisible where bright ≈ normal
        // (conhost/PuTTY). The gutter foreground must be a neutral (black/white) and differ from the
        // gutter background, which is the add/remove background it is drawn over.
        foreach (var (gutter, background) in new[]
                 {
                     (palette.AddedGutterForeground, palette.AddedBackground),
                     (palette.RemovedGutterForeground, palette.RemovedBackground),
                 })
        {
            Assert.True(gutter == White || gutter == Black, "gutter foreground must be a neutral");
            Assert.NotEqual(background, gutter);
        }
    }

    public static TheoryData<DiffPalette> ContrastingGutterPresets() =>
        new() { DiffPalette.Dark16, DiffPalette.Light16 };

    [Fact]
    public void Dark16_Pins_Each_Field_To_Its_Nearest_Ansi_Colour()
    {
        var p = DiffPalette.Dark16;

        Assert.Equal(new Color(ColorName16.Green), p.AddedBackground);
        Assert.Equal(new Color(ColorName16.BrightGreen), p.AddedEmphasisBackground);
        Assert.Equal(new Color(ColorName16.Red), p.RemovedBackground);
        Assert.Equal(new Color(ColorName16.BrightRed), p.RemovedEmphasisBackground);
        Assert.Equal(White, p.AddedGutterForeground);
        Assert.Equal(White, p.RemovedGutterForeground);
        Assert.Equal(new Color(ColorName16.Yellow), p.SearchHitBackground);
    }

    [Fact]
    public void Light16_Pins_Each_Field_To_Its_Nearest_Ansi_Colour()
    {
        var p = DiffPalette.Light16;

        Assert.Equal(new Color(ColorName16.BrightGreen), p.AddedBackground);
        Assert.Equal(new Color(ColorName16.Green), p.AddedEmphasisBackground);
        Assert.Equal(new Color(ColorName16.BrightRed), p.RemovedBackground);
        Assert.Equal(new Color(ColorName16.Red), p.RemovedEmphasisBackground);
        Assert.Equal(Black, p.AddedGutterForeground);
        Assert.Equal(Black, p.RemovedGutterForeground);
        Assert.Equal(new Color(ColorName16.BrightYellow), p.SearchHitBackground);
    }

    [Theory]
    [MemberData(nameof(SixteenColourPresets))]
    public void Sixteen_Colour_Presets_Use_Only_ColorName16_Representable_Colours(DiffPalette palette)
    {
        foreach (var color in NonNullColors(palette))
        {
            // A colour is 16-representable iff it round-trips through its nearest ColorName16.
            Assert.Equal(color, new Color(color.GetClosestNamedColor16()));
        }
    }

    public static TheoryData<DiffPalette> SixteenColourPresets() =>
        new() { DiffPalette.Dark16, DiffPalette.Light16, DiffPalette.Mono };

    private static IEnumerable<Color> NonNullColors(DiffPalette p) =>
        new Color?[]
        {
            p.AddedBackground, p.AddedEmphasisBackground, p.RemovedBackground, p.RemovedEmphasisBackground,
            p.AddedGutterForeground, p.RemovedGutterForeground, p.SearchHitBackground, p.SearchHitForeground,
        }.Where(c => c is not null).Select(c => c!.Value);
}
