using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Theming;

/// <summary>
/// The diff pane's colours, which live outside Terminal.Gui's fixed scheme roles and so are
/// cobalt-owned (ADR 0010). Resolved per active theme and read by <c>DiffListDataSource</c> on
/// each render, so switching themes recolours the diff live. <see cref="Dark"/> is the original
/// look, so an empty config (default <see cref="Core.Config.ThemeChoice.Dark"/>) is unchanged.
///
/// <para>A <c>null</c> background or gutter foreground means <em>inherit the context/normal
/// value</em> rather than paint a tint — used by <see cref="Mono"/> so a monochrome (or
/// <c>NO_COLOR</c>) diff carries add/remove by the <c>+</c>/<c>-</c> sign gutters alone and stays
/// legible on any chrome (ADR 0019 extension). <see cref="SearchHitForeground"/> is <c>null</c>
/// for presets where the run keeps its own foreground, and set only where the hit background needs
/// a paired foreground to stay readable.</para>
/// </summary>
public sealed record DiffPalette(
    Color? AddedBackground,
    Color? AddedEmphasisBackground,
    Color? RemovedBackground,
    Color? RemovedEmphasisBackground,
    Color? AddedGutterForeground,
    Color? RemovedGutterForeground,
    Color SearchHitBackground,
    Color? SearchHitForeground = null)
{
    /// <summary>The dark preset — byte-for-byte the colours cobalt shipped before theming.</summary>
    public static DiffPalette Dark { get; } = new(
        AddedBackground: new Color("#123a12"),
        AddedEmphasisBackground: new Color("#1e6b1e"),
        RemovedBackground: new Color("#3a1212"),
        RemovedEmphasisBackground: new Color("#6b2020"),
        AddedGutterForeground: new Color(ColorName16.BrightGreen),
        RemovedGutterForeground: new Color(ColorName16.BrightRed),
        SearchHitBackground: new Color("#6b5a00"));

    /// <summary>The light preset — diff colours readable on a light background.</summary>
    public static DiffPalette Light { get; } = new(
        AddedBackground: new Color("#d6f5d6"),
        AddedEmphasisBackground: new Color("#a6e0a6"),
        RemovedBackground: new Color("#f8d6d6"),
        RemovedEmphasisBackground: new Color("#e8a6a6"),
        AddedGutterForeground: new Color("#1e6b1e"),
        RemovedGutterForeground: new Color("#8a1f1f"),
        SearchHitBackground: new Color("#fff2a8"));

    /// <summary>
    /// The dark preset degraded to the 16 ANSI colours — the nearest <see cref="ColorName16"/> to
    /// each truecolor tint, for terminals that report <see cref="ColorSupport.Ansi16"/> (ADR 0019
    /// extension). The <c>+</c>/<c>-</c> gutter signs use a contrasting <see cref="ColorName16.White"/>
    /// on the darker green/red gutter background — not a same-hue bright variant, which disappears on
    /// schemes where bright ≈ normal (conhost/PuTTY).
    /// </summary>
    public static DiffPalette Dark16 { get; } = new(
        AddedBackground: new Color(ColorName16.Green),
        AddedEmphasisBackground: new Color(ColorName16.BrightGreen),
        RemovedBackground: new Color(ColorName16.Red),
        RemovedEmphasisBackground: new Color(ColorName16.BrightRed),
        AddedGutterForeground: new Color(ColorName16.White),
        RemovedGutterForeground: new Color(ColorName16.White),
        SearchHitBackground: new Color(ColorName16.Yellow),
        SearchHitForeground: new Color(ColorName16.Black));

    /// <summary>
    /// The light preset degraded to the 16 ANSI colours for <see cref="ColorSupport.Ansi16"/>
    /// terminals. The bright variant is the lighter shade here, so it carries the base background
    /// and the plain variant supplies the darker emphasis. Gutter signs use a contrasting
    /// <see cref="ColorName16.Black"/> on the lighter green/red gutter background (ADR 0019 extension).
    /// </summary>
    public static DiffPalette Light16 { get; } = new(
        AddedBackground: new Color(ColorName16.BrightGreen),
        AddedEmphasisBackground: new Color(ColorName16.Green),
        RemovedBackground: new Color(ColorName16.BrightRed),
        RemovedEmphasisBackground: new Color(ColorName16.Red),
        AddedGutterForeground: new Color(ColorName16.Black),
        RemovedGutterForeground: new Color(ColorName16.Black),
        SearchHitBackground: new Color(ColorName16.BrightYellow),
        SearchHitForeground: new Color(ColorName16.Black));

    /// <summary>
    /// The monochrome preset for <see cref="ColorSupport.None"/> (a mono terminal or
    /// <c>NO_COLOR</c>). Every diff background is <c>null</c> — the row inherits the terminal's
    /// normal background, so no colour tint stripes the diff — and the gutter foregrounds are
    /// <c>null</c> too, so the <c>+</c>/<c>-</c> signs read in the normal foreground. The whole
    /// meaning rides on the sign glyphs and attribute emphasis, keeping the diff legible without
    /// colour (ADR 0019 extension). A search hit reverses to black-on-white so the match still
    /// stands out against the untinted body.
    /// </summary>
    public static DiffPalette Mono { get; } = new(
        AddedBackground: null,
        AddedEmphasisBackground: null,
        RemovedBackground: null,
        RemovedEmphasisBackground: null,
        AddedGutterForeground: null,
        RemovedGutterForeground: null,
        SearchHitBackground: new Color(ColorName16.White),
        SearchHitForeground: new Color(ColorName16.Black));
}
