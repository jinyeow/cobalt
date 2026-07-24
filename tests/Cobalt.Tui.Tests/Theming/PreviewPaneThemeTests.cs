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
}
