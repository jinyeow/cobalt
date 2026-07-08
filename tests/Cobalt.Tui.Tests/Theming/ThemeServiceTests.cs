using Cobalt.Core.Config;
using Cobalt.Tui.Theming;
using Terminal.Gui.Configuration;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// State/flow guards for <see cref="ThemeService"/> (the rendered colours are PTY/manual per
/// ADR 0010). Touches Terminal.Gui's global <c>ConfigurationManager</c>/<c>ThemeManager</c>
/// statics, so the theming tests share a non-parallel collection to avoid interleaving.
/// </summary>
[Collection(nameof(ThemeServiceTests))]
[CollectionDefinition(nameof(ThemeServiceTests), DisableParallelization = true)]
public class ThemeServiceTests
{
    [Fact]
    public void Enable_Merges_Library_Themes_Onto_The_HardCoded_Default()
    {
        ThemeService.Enable();

        // The frozen ThemeResolver names "Default" (dark/base) and "Light"; enabling the library
        // resources must MERGE their themes onto the hard-coded set so BOTH keys resolve —
        // otherwise ThemeManager.Theme = "Default" (or "Light") would point at a missing key.
        // (The active theme is asserted in Apply_Dark below; global TG statics leak across tests
        // in one process, so the *current* name is not order-independent here.)
        var themes = ThemeManager.Themes;
        Assert.NotNull(themes);
        Assert.Contains("Default", themes.Keys);
        Assert.Contains("Light", themes.Keys);
    }

    [Fact]
    public void Apply_Light_Sets_CurrentPalette_To_The_Light_Preset()
    {
        ThemeService.Enable();
        var light = ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown);

        ThemeService.Apply(light);

        Assert.Equal(light.Diff, ThemeService.CurrentPalette);
        Assert.NotEqual(DiffPalette.Dark, ThemeService.CurrentPalette);
    }

    [Fact]
    public void Apply_Dark_Restores_The_Original_Palette()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown));

        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown));

        Assert.Equal(DiffPalette.Dark, ThemeService.CurrentPalette);
        // "Default" is a live, selectable key after the merge — the active theme really switched.
        Assert.Equal("Default", ThemeManager.GetCurrentThemeName());
    }
}
