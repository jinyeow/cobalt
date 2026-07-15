using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
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

    [Fact]
    public void A_Composition_Reused_Across_Renders_Still_Recolours_With_The_Theme()
    {
        // ADR 0019: DiffStyleCache hands the same StyledLine back on every render, so the pane
        // would freeze on one theme if a composition pinned any colour. It must not — a
        // StyledLine carries roles and diff kinds only, and the palette is read at paint.
        var cache = new Cobalt.Core.Text.DiffStyleCache();
        cache.Prepare(
            [new DiffLine(DiffLineKind.Added, null, 1, "x")],
            Language.CSharp,
            new HashSet<int>(),
            new HashSet<int>());
        var cached = cache.Unified(0);
        var gutter = cached.Runs[0].Style;

        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Dark, OsTheme.Unknown));
        var dark = new DiffListDataSource([cached]).Map(gutter, Normal, RoleForeground);
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Light, OsTheme.Unknown));
        var light = new DiffListDataSource([cached]).Map(gutter, Normal, RoleForeground);

        Assert.Same(cached, cache.Unified(0)); // the same cached line drew both
        Assert.Equal(DiffPalette.Dark.AddedBackground, dark.Background);
        Assert.Equal(DiffPalette.Light.AddedBackground, light.Background);
    }

    [Fact]
    public void Render_Resolves_Each_Distinct_Role_At_Most_Once_Per_Row()
    {
        // A row is a gutter run plus six code runs spanning only two distinct token kinds
        // (Identifier, Keyword). Today GetAttributeForRole is called once per non-gutter run
        // (six calls); it should instead be resolved once per distinct role used.
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Dark, OsTheme.Unknown));
        var runs = new List<StyledRun>
        {
            new(0, 2, new RunStyle(TokenKind.Plain, DiffLineKind.Context, Emphasis: false, IsGutter: true)),
            new(2, 1, new RunStyle(TokenKind.Identifier, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
            new(3, 1, new RunStyle(TokenKind.Identifier, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
            new(4, 1, new RunStyle(TokenKind.Keyword, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
            new(5, 1, new RunStyle(TokenKind.Identifier, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
            new(6, 1, new RunStyle(TokenKind.Keyword, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
            new(7, 1, new RunStyle(TokenKind.Identifier, DiffLineKind.Context, Emphasis: false, IsGutter: false)),
        };
        var line = new StyledLine("+ aaaaaa", runs);
        var source = new DiffListDataSource([line]);
        var listView = new ListView { SchemeName = "Base", Width = 40, Height = 1 };
        var invocations = 0;
        listView.GettingAttributeForRole += (_, _) => invocations++;

        source.Render(listView, selected: false, item: 0, col: 0, row: 0, width: 40, viewportX: 0);

        // Normal (once) + CodeIdentifier + CodeKeyword = at most 3 distinct roles resolved.
        Assert.True(invocations <= 3, $"expected at most 3 role resolutions, got {invocations}");
    }
}
