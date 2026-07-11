using Terminal.Gui.Configuration;

namespace Cobalt.Tui.Theming;

/// <summary>
/// The single runtime seam onto Terminal.Gui's theming. <see cref="Enable"/> turns on
/// Terminal.Gui's <c>ConfigurationManager</c> scoped to the library's own embedded config
/// (never the user's <c>~/.tui</c>, <c>./.tui</c>, <c>TUI_CONFIG</c>, or app resources), so
/// cobalt owns the theme set; <see cref="Apply"/> switches the active TG theme and records the
/// cobalt-owned <see cref="DiffPalette"/>. <see cref="CurrentPalette"/> is the ambient palette
/// the diff renderer reads each frame, so a theme switch recolours the diff without threading a
/// palette through every construction site (the theme is global, so ambient is correct).
///
/// <para>Obsolete API note: <c>ConfigurationManager</c> is <c>[Obsolete]</c> (CS0618) in TG
/// 2.4.16, yet it remains the only runtime-theming API — its Modular Extension Configuration
/// (MEC) replacement can't own theme data yet (TG #5416). This file is the single place that
/// warning is suppressed, scoped tightly to the two calls that touch it.</para>
/// </summary>
public static class ThemeService
{
    private static DiffPalette _currentPalette = DiffPalette.Dark;

    /// <summary>
    /// The diff pane's colours for the active theme, read by <c>DiffListDataSource</c> on each
    /// render. Defaults to <see cref="DiffPalette.Dark"/> until the first <see cref="Apply"/>,
    /// so the pre-theming look is unchanged before startup wires anything up.
    /// </summary>
    public static DiffPalette CurrentPalette => _currentPalette;

    /// <summary>
    /// Enables Terminal.Gui's <c>ConfigurationManager</c> scoped to
    /// <see cref="ConfigLocations.LibraryResources"/> only — the themes embedded in
    /// <c>Terminal.Gui.dll</c> merged onto its hard-coded defaults. No user or app config
    /// location is read, so an empty user config cannot shift cobalt's look. Call once before
    /// <c>Application.Init</c>.
    /// </summary>
    public static void Enable()
    {
#pragma warning disable CS0618 // ConfigurationManager is [Obsolete] but is TG 2.4.16's only runtime-theming API (TG #5416).
        ConfigurationManager.Enable(ConfigLocations.LibraryResources);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Switches to <paramref name="preset"/>: sets the active Terminal.Gui theme (chrome and the
    /// syntax <c>VisualRole.Code*</c> foregrounds re-resolve live), applies it, and records the
    /// diff palette as <see cref="CurrentPalette"/>. A subsequent repaint
    /// (<c>app.LayoutAndDraw(true)</c>) shows the change without recreating any views.
    /// </summary>
    public static void Apply(ThemePreset preset)
    {
        ThemeManager.Theme = preset.TgThemeName;
#pragma warning disable CS0618 // ConfigurationManager is [Obsolete] but is TG 2.4.16's only runtime-theming API (TG #5416).
        ConfigurationManager.Apply();
#pragma warning restore CS0618
        _currentPalette = preset.Diff;
    }
}
