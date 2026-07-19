using Terminal.Gui.Drawing;

namespace Cobalt.Tui.Theming;

/// <summary>
/// The diff pane's colours, which live outside Terminal.Gui's fixed scheme roles and so are
/// cobalt-owned (ADR 0010). Resolved per active theme and read by <c>DiffListDataSource</c> on
/// each render, so switching themes recolours the diff live. <see cref="Dark"/> is the original
/// look, so an empty config (default <see cref="Core.Config.ThemeChoice.Dark"/>) is unchanged.
/// </summary>
public sealed record DiffPalette(
    Color AddedBackground,
    Color AddedEmphasisBackground,
    Color RemovedBackground,
    Color RemovedEmphasisBackground,
    Color AddedGutterForeground,
    Color RemovedGutterForeground,
    Color SearchHitBackground)
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
    /// extension). Emphasis uses the bright variant so it still stands out one step from the base.
    /// </summary>
    public static DiffPalette Dark16 { get; } = new(
        AddedBackground: new Color(ColorName16.Green),
        AddedEmphasisBackground: new Color(ColorName16.BrightGreen),
        RemovedBackground: new Color(ColorName16.Red),
        RemovedEmphasisBackground: new Color(ColorName16.BrightRed),
        AddedGutterForeground: new Color(ColorName16.BrightGreen),
        RemovedGutterForeground: new Color(ColorName16.BrightRed),
        SearchHitBackground: new Color(ColorName16.Yellow));

    /// <summary>
    /// The light preset degraded to the 16 ANSI colours for <see cref="ColorSupport.Ansi16"/>
    /// terminals. The bright variant is the lighter shade here, so it carries the base background
    /// and the plain variant supplies the darker emphasis (ADR 0019 extension).
    /// </summary>
    public static DiffPalette Light16 { get; } = new(
        AddedBackground: new Color(ColorName16.BrightGreen),
        AddedEmphasisBackground: new Color(ColorName16.Green),
        RemovedBackground: new Color(ColorName16.BrightRed),
        RemovedEmphasisBackground: new Color(ColorName16.Red),
        AddedGutterForeground: new Color(ColorName16.Green),
        RemovedGutterForeground: new Color(ColorName16.Red),
        SearchHitBackground: new Color(ColorName16.BrightYellow));

    /// <summary>
    /// The monochrome preset for <see cref="ColorSupport.None"/> (a mono terminal or
    /// <c>NO_COLOR</c>). No background tint distinguishes add from remove — the renderer's
    /// <c>+</c>/<c>-</c> sign gutters and attribute emphasis carry the whole meaning, so the diff
    /// stays legible without colour (ADR 0019 extension). A search hit reverses to white so it
    /// still stands out against the untinted body.
    /// </summary>
    public static DiffPalette Mono { get; } = new(
        AddedBackground: new Color(ColorName16.Black),
        AddedEmphasisBackground: new Color(ColorName16.Black),
        RemovedBackground: new Color(ColorName16.Black),
        RemovedEmphasisBackground: new Color(ColorName16.Black),
        AddedGutterForeground: new Color(ColorName16.White),
        RemovedGutterForeground: new Color(ColorName16.White),
        SearchHitBackground: new Color(ColorName16.White));
}
