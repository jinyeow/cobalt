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
}
