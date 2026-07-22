using System.Collections;
using System.Collections.Specialized;
using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;
using Cobalt.Tui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Cobalt.Tui.Screens;

/// <summary>
/// A colored <see cref="IListDataSource"/> for the diff pane (ADR 0010). Draws
/// each row as a sequence of styled runs — foreground from the syntax token's
/// theme role (<see cref="VisualRole"/> Code*), background from the diff state
/// (context/added/removed plus emphasis for changed words). The composition is
/// pure (<see cref="DiffLineStyler"/>); this adapter only maps a
/// <see cref="RunStyle"/> to a Terminal.Gui <see cref="Attribute"/> and blits it.
/// The selected row renders in the theme's Focus attribute so the cursor bar is
/// unmistakable. Only PTY-verifiable code lives here.
///
/// <para>The diff-state colours come from a <see cref="DiffPalette"/> (the diff pane lives
/// outside TG's fixed scheme roles, ADR 0010). <paramref name="palette"/> is read on every
/// mapped run, defaulting to <see cref="ThemeService.CurrentPalette"/>, so switching the theme
/// recolours the diff live without recreating the source.</para>
/// </summary>
public sealed class DiffListDataSource(IReadOnlyList<StyledLine> lines, Func<DiffPalette>? palette = null)
    : IListDataSource
{
    private readonly IReadOnlyList<StyledLine> _lines = lines;
    private readonly Func<DiffPalette> _palette = palette ?? (() => ThemeService.CurrentPalette);
    private static readonly int TokenKindCount = Enum.GetValues<TokenKind>().Length;

    // Reused across rows so the diff pane does not allocate a Color?[] per drawn row every frame.
    // Cleared per row (Render is single-threaded on the UI thread) so a re-render after a :theme
    // switch re-resolves each Code* foreground rather than serving the palette it first drew under.
    private readonly Color?[] _roleForegrounds = new Color?[TokenKindCount];

    // Lazily cached per-line slices of the unclipped runs, so a row redrawn on every repaint
    // (vertical scroll, selection move) that does not rebuild the source reuses its substrings
    // rather than re-cutting them each frame; only clipped runs (viewportX/width) Substring fresh.
    // Bounded by the rows actually drawn, and discarded when the source is replaced (RENDER-3).
    private readonly string?[]?[] _runSlices = new string?[lines.Count][];

    public int Count => _lines.Count;

    /// <summary>Test seam: the composed lines this source draws, by row.</summary>
    internal IReadOnlyList<StyledLine> Lines => _lines;

    /// <summary>
    /// Test seam: the diff palette this source paints with, read fresh — the production path
    /// constructs it without an override, so this is what makes <c>:theme</c> live (ADR 0019).
    /// </summary>
    internal DiffPalette Palette => _palette();

    public int MaxItemLength { get; } = lines.Count == 0 ? 0 : lines.Max(l => l.DisplayText.Length);

    public bool SuspendCollectionChangedEvent { get; set; }

    public event NotifyCollectionChangedEventHandler CollectionChanged
    {
        add { }
        remove { }
    }

    public bool IsMarked(int item) => false;

    public void SetMark(int item, bool value)
    {
        // The diff pane has no multi-select; marks are unused.
    }

    public IList ToList() => _lines.Select(l => l.DisplayText).ToList();

    public bool RenderMark(ListView listView, int item, int row, bool marked, bool selected) => false;

    public void Render(
        ListView listView, bool selected, int item, int col, int row, int width, int viewportX)
    {
        listView.Move(col, row);
        if (item < 0 || item >= _lines.Count)
        {
            return;
        }
        var styled = _lines[item];
        var lineKind = styled.Runs.Count > 0 ? styled.Runs[0].Style.LineKind : DiffLineKind.Context;

        if (selected)
        {
            // Whole row in the Focus role — mirrors ListWrapper's selection bar.
            listView.SetAttribute(listView.GetAttributeForRole(VisualRole.Focus));
            var visible = Clip(styled.DisplayText, viewportX, width);
            listView.AddStr(visible);
            Pad(listView, width - visible.Length);
            return;
        }

        var normal = listView.GetAttributeForRole(VisualRole.Normal);
        var palette = _palette();
        // Resolve each distinct Code* role's foreground at most once per row, not once per
        // run — a row can carry dozens of runs (e.g. many Identifier tokens) over a handful
        // of roles, and GetAttributeForRole does scheme resolution plus an event allocation.
        // The array is reused across rows; clear it so each row (and each redraw after a theme
        // switch) resolves fresh.
        Array.Clear(_roleForegrounds);
        var drawn = 0;
        for (var i = 0; i < styled.Runs.Count; i++)
        {
            var runItem = styled.Runs[i];
            var start = Math.Max(runItem.Start, viewportX);
            var end = Math.Min(runItem.Start + runItem.Length, viewportX + width);
            if (end <= start)
            {
                continue;
            }
            // The token foreground comes from the theme's Code* role (gutter runs ignore it);
            // resolve it here so Map stays a pure palette mapping.
            var roleForeground = runItem.Style.IsGutter
                ? normal.Foreground
                : ResolveRoleForeground(listView, _roleForegrounds, runItem.Style.Token);
            listView.SetAttribute(Map(runItem.Style, normal, roleForeground, palette));
            listView.AddStr(SliceFor(item, i, runItem, styled.DisplayText, start, end));
            drawn += end - start;
        }

        // Full-width diff tint: pad the rest of the row in the line-kind background.
        listView.SetAttribute(new Attribute(normal.Foreground, BackgroundFor(lineKind, emphasis: false, normal.Background, palette)));
        Pad(listView, width - drawn);
    }

    public void Dispose()
    {
        // Nothing owned; the source is replaced wholesale on refresh.
    }

    /// <summary>
    /// The string handed to <c>AddStr</c> for one run (there is no span overload — verified on TG
    /// 2.4.16). When the run is fully on screen — the clamped [<paramref name="start"/>,
    /// <paramref name="end"/>) equals the run's own [Start, Start+Length) — its slice equals the
    /// run's text, so it is cut once and cached per line and reused on every later repaint; a
    /// clipped run is Substring'd fresh each time.
    /// </summary>
    private string SliceFor(int item, int runIndex, StyledRun run, string display, int start, int end)
    {
        var unclipped = start == run.Start && end == run.Start + run.Length;
        if (!unclipped)
        {
            return display.Substring(start, end - start);
        }
        var slices = _runSlices[item] ??= new string?[_lines[item].Runs.Count];
        return slices[runIndex] ??= display.Substring(run.Start, run.Length);
    }

    /// <summary>Test seam: the cached unclipped-run slices for a row (null until the row is drawn).</summary>
    internal IReadOnlyList<string?>? RunSlicesFor(int item) => _runSlices[item];

    private static string Clip(string text, int viewportX, int width)
    {
        if (viewportX >= text.Length)
        {
            return "";
        }
        var take = Math.Min(width, text.Length - viewportX);
        return text.Substring(viewportX, take);
    }

    private static void Pad(ListView listView, int count)
    {
        for (var i = 0; i < count; i++)
        {
            listView.AddRune(' ');
        }
    }

    /// <summary>
    /// Maps a <see cref="RunStyle"/> to its <see cref="Attribute"/> using <paramref name="palette"/>.
    /// The theme-role parts are supplied by the caller: <paramref name="normal"/> is
    /// <see cref="VisualRole.Normal"/> (context foreground/background) and
    /// <paramref name="roleForeground"/> is the token's Code* foreground — so this stays a pure
    /// palette mapping. <see cref="Render"/> reads the palette once per row and passes it through,
    /// so live <c>:theme</c> switching works without re-reading it per run.
    /// </summary>
    internal static Attribute Map(RunStyle style, Attribute normal, Color roleForeground, DiffPalette palette)
    {
        var foreground = style.IsGutter
            ? style.LineKind switch
            {
                // A null gutter foreground inherits normal (mono: the +/- sign carries the meaning).
                DiffLineKind.Added => palette.AddedGutterForeground ?? normal.Foreground,
                DiffLineKind.Removed => palette.RemovedGutterForeground ?? normal.Foreground,
                _ => normal.Foreground,
            }
            : roleForeground;

        // A search hit wins the background so matches stand out over the diff tint; its paired
        // foreground (when set) keeps the match legible where the run's own foreground would clash
        // with the hit background (mono: black-on-white).
        var background = style.SearchHit
            ? palette.SearchHitBackground
            : BackgroundFor(style.LineKind, style.Emphasis, normal.Background, palette);
        if (style.SearchHit && palette.SearchHitForeground is { } hitForeground)
        {
            foreground = hitForeground;
        }
        return new Attribute(foreground, background);
    }

    /// <summary>
    /// Resolves the Code* foreground for <paramref name="token"/>, caching it in
    /// <paramref name="cache"/> (indexed by <see cref="TokenKind"/>) so a role already seen in
    /// this row is not re-resolved for every run that carries it.
    /// </summary>
    private static Color ResolveRoleForeground(ListView listView, Color?[] cache, TokenKind token)
    {
        var index = (int)token;
        return cache[index] ??= listView.GetAttributeForRole(RoleFor(token)).Foreground;
    }

    private static VisualRole RoleFor(TokenKind token) => token switch
    {
        TokenKind.Keyword => VisualRole.CodeKeyword,
        TokenKind.String => VisualRole.CodeString,
        TokenKind.Comment => VisualRole.CodeComment,
        TokenKind.Number => VisualRole.CodeNumber,
        TokenKind.Operator => VisualRole.CodeOperator,
        TokenKind.Punctuation => VisualRole.CodePunctuation,
        TokenKind.Identifier => VisualRole.CodeIdentifier,
        _ => VisualRole.Normal,
    };

    // A null diff background inherits the context/normal background — the mono palette uses this so
    // added/removed rows carry no colour tint (the +/- sign gutters carry add/remove instead).
    private static Color BackgroundFor(DiffLineKind kind, bool emphasis, Color contextBackground, DiffPalette palette) =>
        kind switch
        {
            DiffLineKind.Added => (emphasis ? palette.AddedEmphasisBackground : palette.AddedBackground) ?? contextBackground,
            DiffLineKind.Removed => (emphasis ? palette.RemovedEmphasisBackground : palette.RemovedBackground) ?? contextBackground,
            _ => contextBackground,
        };
}
