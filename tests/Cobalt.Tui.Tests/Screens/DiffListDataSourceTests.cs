using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// State/flow guards for the diff renderer's palette wiring (the on-screen pixels are PTY/manual
/// per ADR 0010). Injects a distinctive <see cref="DiffPalette"/> and asserts the run attributes
/// it maps come from that palette, not the old hard-coded colours — so switching themes recolours
/// the diff live. The listView-resolved parts (token foreground, context background) are passed in
/// as <c>normal</c>/<c>roleForeground</c>, so no driver/Application.Init is needed.
/// </summary>
// The ambient-default case reads ThemeService's global palette, so share the theming
// collection (non-parallel) to keep that read from racing another test's Apply.
[Collection(nameof(Cobalt.Tui.Tests.Theming.ThemeServiceTests))]
public class DiffListDataSourceTests
{
    private static readonly DiffPalette Custom = new(
        AddedBackground: new Color("#010203"),
        AddedEmphasisBackground: new Color("#040506"),
        RemovedBackground: new Color("#070809"),
        RemovedEmphasisBackground: new Color("#0a0b0c"),
        AddedGutterForeground: new Color("#0d0e0f"),
        RemovedGutterForeground: new Color("#101112"),
        SearchHitBackground: new Color("#131415"));

    private static DiffListDataSource Source() => new([], () => Custom);

    private static readonly Attribute Normal =
        new(new Color("#aaaaaa"), new Color("#222222"));

    private static readonly Color RoleForeground = new Color("#bbbbbb");

    [Fact]
    public void Added_Gutter_Uses_The_Injected_Gutter_Foreground_And_Added_Background()
    {
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: true);

        var attr = Source().Map(style, Normal, RoleForeground);

        Assert.Equal(Custom.AddedGutterForeground, attr.Foreground);
        Assert.Equal(Custom.AddedBackground, attr.Background);
    }

    [Fact]
    public void Removed_Emphasis_Uses_The_Injected_Removed_Emphasis_Background()
    {
        var style = new RunStyle(TokenKind.Identifier, DiffLineKind.Removed, Emphasis: true, IsGutter: false);

        var attr = Source().Map(style, Normal, RoleForeground);

        Assert.Equal(RoleForeground, attr.Foreground);
        Assert.Equal(Custom.RemovedEmphasisBackground, attr.Background);
    }

    [Fact]
    public void Search_Hit_Uses_The_Injected_Search_Hit_Background()
    {
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: false)
        {
            SearchHit = true,
        };

        var attr = Source().Map(style, Normal, RoleForeground);

        Assert.Equal(Custom.SearchHitBackground, attr.Background);
    }

    [Fact]
    public void Context_Falls_Back_To_The_Normal_Background()
    {
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Context, Emphasis: false, IsGutter: false);

        var attr = Source().Map(style, Normal, RoleForeground);

        Assert.Equal(Normal.Background, attr.Background);
    }

    [Fact]
    public void Defaults_To_The_Ambient_Current_Palette()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Dark, OsTheme.Unknown));
        var ambient = new DiffListDataSource([]);
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: true);

        var attr = ambient.Map(style, Normal, RoleForeground);

        // With no injected Func, the source reads ThemeService.CurrentPalette (dark here).
        Assert.Equal(DiffPalette.Dark.AddedGutterForeground, attr.Foreground);
        Assert.Equal(DiffPalette.Dark.AddedBackground, attr.Background);
    }
}
