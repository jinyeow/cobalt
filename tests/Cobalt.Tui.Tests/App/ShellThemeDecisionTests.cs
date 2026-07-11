using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Theming;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell's OS-follow decision (CobaltShell.OsFollowPreset): an OS light/dark flip drives an
/// apply only while the user follows the system (theme = system); a fixed dark/light choice
/// ignores it. Pure/headless — the actual repaint is PTY/manual per ADR 0010.
/// </summary>
public class ShellThemeDecisionTests
{
    [Fact]
    public void Following_System_Applies_The_Light_Preset_When_The_Os_Goes_Light()
    {
        var preset = CobaltShell.OsFollowPreset(ThemeChoice.System, OsTheme.Light);

        Assert.NotNull(preset);
        Assert.Equal("Light", preset.TgThemeName);
        Assert.NotEqual(DiffPalette.Dark, preset.Diff);
    }

    [Fact]
    public void Following_System_Applies_The_Dark_Preset_When_The_Os_Goes_Dark()
    {
        var preset = CobaltShell.OsFollowPreset(ThemeChoice.System, OsTheme.Dark);

        Assert.NotNull(preset);
        Assert.Equal("Default", preset.TgThemeName);
        Assert.Equal(DiffPalette.Dark, preset.Diff);
    }

    [Theory]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Light)]
    public void A_Fixed_Theme_Ignores_Os_Changes(ThemeChoice fixedChoice)
    {
        Assert.Null(CobaltShell.OsFollowPreset(fixedChoice, OsTheme.Light));
        Assert.Null(CobaltShell.OsFollowPreset(fixedChoice, OsTheme.Dark));
    }
}
