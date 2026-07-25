using Cobalt.Core.Config;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// The preview pane's read-only body must be legible in the shipped <c>dark</c> theme (#66).
/// A ReadOnly TextView paints its text with <see cref="VisualRole.ReadOnly"/>; under the dark
/// Base scheme that role is gray-on-gray, so an unscheme'd pane renders invisible. Touches the
/// global TG theme statics, so it joins the non-parallel theming collection.
/// </summary>
[Collection(nameof(ThemeServiceTests))]
public class PreviewPaneThemeTests
{
    [Fact]
    public void Body_Is_Legible_In_The_Dark_Theme()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown));
        using var pane = new PreviewPane();

        // The body is ReadOnly, so its text is drawn with the ReadOnly role — the one role that
        // collapses to gray-on-gray under the dark Base scheme (Normal's None/None renders as
        // readable terminal defaults, so it is not the culprit).
        var readOnly = pane.Body.GetAttributeForRole(VisualRole.ReadOnly);

        Assert.NotEqual(readOnly.Background, readOnly.Foreground);
    }

    [Fact]
    public void Body_Is_Legible_In_The_Light_Theme()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown));
        using var pane = new PreviewPane();

        // The Dialog scheme is theme-agnostic, so the same fix must hold in light: its ReadOnly
        // role stays legible (fg≠bg) rather than collapsing the way the dark Base role did.
        var readOnly = pane.Body.GetAttributeForRole(VisualRole.ReadOnly);

        Assert.NotEqual(readOnly.Background, readOnly.Foreground);
    }

    [Fact]
    public void Border_Is_Legible_In_The_Dark_Theme()
    {
        // #68 (Codex HIGH): #66 scheme'd the body only. The border is a Terminal.Gui
        // adornment resolving through the PANE's own GetScheme(), not the body's — an
        // unscheme'd pane would leave the border to inherit whatever ambient scheme
        // surrounds it, the same gray-on-gray trap #66 fixed for the body. Prove it
        // directly rather than inferring it from the body's fix.
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown));
        using var pane = new PreviewPane();

        var border = pane.Border.GetOrCreateView().GetAttributeForRole(VisualRole.Normal);

        Assert.NotEqual(border.Background, border.Foreground);
    }

    [Fact]
    public void Border_Is_Legible_In_The_Light_Theme()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown));
        using var pane = new PreviewPane();

        var border = pane.Border.GetOrCreateView().GetAttributeForRole(VisualRole.Normal);

        Assert.NotEqual(border.Background, border.Foreground);
    }
}
