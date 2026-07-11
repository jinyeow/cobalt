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
}
