using Cobalt.Core.Config;
using Cobalt.Tui.Theming;

namespace Cobalt.Tui.Tests.Theming;

public class ThemeResolverTests
{
    [Fact]
    public void Dark_Resolves_To_Default_Tg_Theme_With_The_Original_Palette()
    {
        var preset = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown);

        Assert.Equal("Default", preset.TgThemeName);
        Assert.Equal(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void Light_Resolves_To_Light_Tg_Theme_With_A_Palette_Distinct_From_Dark()
    {
        var preset = ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown);

        Assert.Equal("Light", preset.TgThemeName);
        Assert.NotEqual(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void System_Following_Light_Os_Resolves_To_The_Light_Preset()
    {
        var preset = ThemeResolver.Resolve(ThemeChoice.System, OsTheme.Light);

        Assert.Equal("Light", preset.TgThemeName);
        Assert.NotEqual(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void System_Following_Dark_Os_Resolves_To_The_Dark_Preset()
    {
        var preset = ThemeResolver.Resolve(ThemeChoice.System, OsTheme.Dark);

        Assert.Equal("Default", preset.TgThemeName);
        Assert.Equal(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void System_Following_Unknown_Os_Falls_Back_To_The_Dark_Preset()
    {
        var preset = ThemeResolver.Resolve(ThemeChoice.System, OsTheme.Unknown);

        Assert.Equal("Default", preset.TgThemeName);
        Assert.Equal(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void Unknown_Choice_Throws_Rather_Than_Silently_Defaulting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThemeResolver.Resolve((ThemeChoice)999, OsTheme.Dark));
    }

    [Fact]
    public void TwoArg_Overload_Assumes_Full_Colour_So_Truecolor_Is_Unchanged()
    {
        var twoArg = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown);
        var threeArg = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown, ColorSupport.Full);

        Assert.Equal(threeArg, twoArg);
        Assert.Equal(DiffPalette.Dark, twoArg.Diff);
    }

    [Fact]
    public void Full_Colour_Keeps_The_Truecolor_Diff_Palette()
    {
        var dark = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown, ColorSupport.Full);
        var light = ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown, ColorSupport.Full);

        Assert.Equal("Default", dark.TgThemeName);
        Assert.Equal(DiffPalette.Dark, dark.Diff);
        Assert.Equal("Light", light.TgThemeName);
        Assert.Equal(DiffPalette.Light, light.Diff);
    }

    [Fact]
    public void Ansi16_Maps_Each_Base_Theme_To_Its_16_Colour_Diff_Palette()
    {
        var dark = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown, ColorSupport.Ansi16);
        var light = ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown, ColorSupport.Ansi16);

        // Chrome name is unchanged (TG themes the chrome; Force16Colors is a startup concern).
        Assert.Equal("Default", dark.TgThemeName);
        Assert.Equal(DiffPalette.Dark16, dark.Diff);
        Assert.Equal("Light", light.TgThemeName);
        Assert.Equal(DiffPalette.Light16, light.Diff);
    }

    [Fact]
    public void Mono_Collapses_Both_Base_Themes_To_The_Sign_Only_Palette()
    {
        var dark = ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown, ColorSupport.None);
        var light = ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Light, ColorSupport.None);

        Assert.Equal(DiffPalette.Mono, dark.Diff);
        Assert.Equal(DiffPalette.Mono, light.Diff);
    }
}
