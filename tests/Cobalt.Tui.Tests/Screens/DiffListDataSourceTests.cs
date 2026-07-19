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

    private static readonly Attribute Normal =
        new(new Color("#aaaaaa"), new Color("#222222"));

    private static readonly Color RoleForeground = new Color("#bbbbbb");

    [Fact]
    public void Added_Gutter_Uses_The_Injected_Gutter_Foreground_And_Added_Background()
    {
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: true);

        var attr = DiffListDataSource.Map(style, Normal, RoleForeground, Custom);

        Assert.Equal(Custom.AddedGutterForeground, attr.Foreground);
        Assert.Equal(Custom.AddedBackground, attr.Background);
    }

    [Fact]
    public void Removed_Emphasis_Uses_The_Injected_Removed_Emphasis_Background()
    {
        var style = new RunStyle(TokenKind.Identifier, DiffLineKind.Removed, Emphasis: true, IsGutter: false);

        var attr = DiffListDataSource.Map(style, Normal, RoleForeground, Custom);

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

        var attr = DiffListDataSource.Map(style, Normal, RoleForeground, Custom);

        Assert.Equal(Custom.SearchHitBackground, attr.Background);
    }

    [Fact]
    public void Mono_Added_And_Removed_Rows_Inherit_The_Context_Background_So_No_Tint()
    {
        // NO_COLOR / mono must not stripe the diff with a hard-coded background — the +/- sign
        // gutters carry add/remove, and the body keeps the terminal's normal background so it stays
        // legible under any chrome (ADR 0019 extension).
        var added = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: false);
        var addedEmphasis = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: true, IsGutter: false);
        var removed = new RunStyle(TokenKind.Plain, DiffLineKind.Removed, Emphasis: false, IsGutter: false);
        var removedEmphasis = new RunStyle(TokenKind.Plain, DiffLineKind.Removed, Emphasis: true, IsGutter: false);

        Assert.Equal(Normal.Background, DiffListDataSource.Map(added, Normal, RoleForeground, DiffPalette.Mono).Background);
        Assert.Equal(Normal.Background, DiffListDataSource.Map(addedEmphasis, Normal, RoleForeground, DiffPalette.Mono).Background);
        Assert.Equal(Normal.Background, DiffListDataSource.Map(removed, Normal, RoleForeground, DiffPalette.Mono).Background);
        Assert.Equal(Normal.Background, DiffListDataSource.Map(removedEmphasis, Normal, RoleForeground, DiffPalette.Mono).Background);
    }

    [Fact]
    public void Mono_Gutter_Sign_Uses_The_Normal_Foreground_On_The_Untinted_Background()
    {
        // The +/- glyph must read on the inherited background — its colour cannot carry meaning,
        // so it falls back to the theme's normal foreground rather than a fixed hue.
        var addedGutter = new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: true);

        var attr = DiffListDataSource.Map(addedGutter, Normal, RoleForeground, DiffPalette.Mono);

        Assert.Equal(Normal.Foreground, attr.Foreground);
        Assert.Equal(Normal.Background, attr.Background);
    }

    [Fact]
    public void Context_Falls_Back_To_The_Normal_Background()
    {
        var style = new RunStyle(TokenKind.Plain, DiffLineKind.Context, Emphasis: false, IsGutter: false);

        var attr = DiffListDataSource.Map(style, Normal, RoleForeground, Custom);

        Assert.Equal(Normal.Background, attr.Background);
    }

    [Fact]
    public void Defaults_To_The_Ambient_Current_Palette_Read_Fresh()
    {
        // The production path constructs the source with no palette override, so this default is
        // what paints the diff — and it must be read fresh on every use, not captured, or a live
        // :theme switch would leave the pane on the palette it was built under (ADR 0019).
        ThemeService.Enable();
        var ambient = new DiffListDataSource([]);

        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Dark, OsTheme.Unknown));
        Assert.Equal(DiffPalette.Dark, ambient.Palette);

        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Light, OsTheme.Unknown));
        Assert.Equal(DiffPalette.Light, ambient.Palette);
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
        var dark = DiffListDataSource.Map(gutter, Normal, RoleForeground, ThemeService.CurrentPalette);
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Light, OsTheme.Unknown));
        var light = DiffListDataSource.Map(gutter, Normal, RoleForeground, ThemeService.CurrentPalette);

        Assert.Same(cached, cache.Unified(0)); // the same cached line drew both
        Assert.Equal(DiffPalette.Dark.AddedBackground, dark.Background);
        Assert.Equal(DiffPalette.Light.AddedBackground, light.Background);
    }

    [Fact]
    public void Render_Caches_Unclipped_Run_Slices_Across_Redraws()
    {
        // An unclipped run (viewportX 0, fits in width) must be sliced once and the substring
        // reused on every later repaint of that row (vertical scroll / selection move that does
        // not rebuild the source), rather than re-Substring'd every frame (RENDER-3).
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(Cobalt.Core.Config.ThemeChoice.Dark, OsTheme.Unknown));
        var runs = new List<StyledRun>
        {
            new(0, 2, new RunStyle(TokenKind.Plain, DiffLineKind.Added, Emphasis: false, IsGutter: true)),
            new(2, 6, new RunStyle(TokenKind.Identifier, DiffLineKind.Added, Emphasis: false, IsGutter: false)),
        };
        var source = new DiffListDataSource([new StyledLine("+ abcdef", runs)]);
        var listView = new ListView { SchemeName = "Base", Width = 40, Height = 1 };

        source.Render(listView, selected: false, item: 0, col: 0, row: 0, width: 40, viewportX: 0);
        // Capture the string instance now — reading the slot again after render 2 would alias the
        // live array, so a ??=→= regression (re-Substring every frame) would still look "Same".
        var sliceAfterFirst = source.RunSlicesFor(0)?[1];
        source.Render(listView, selected: false, item: 0, col: 0, row: 0, width: 40, viewportX: 0);
        var sliceAfterSecond = source.RunSlicesFor(0)?[1];

        Assert.NotNull(sliceAfterFirst);
        Assert.Same(sliceAfterFirst, sliceAfterSecond); // reused the cached substring, not re-cut
        Assert.Equal("abcdef", sliceAfterSecond);       // and it is exactly the run's text
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
