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

    public int Count => _lines.Count;

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
        var drawn = 0;
        foreach (var runItem in styled.Runs)
        {
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
                : listView.GetAttributeForRole(RoleFor(runItem.Style.Token)).Foreground;
            listView.SetAttribute(Map(runItem.Style, normal, roleForeground));
            listView.AddStr(styled.DisplayText.Substring(start, end - start));
            drawn += end - start;
        }

        // Full-width diff tint: pad the rest of the row in the line-kind background.
        listView.SetAttribute(new Attribute(normal.Foreground, BackgroundFor(lineKind, emphasis: false, normal.Background)));
        Pad(listView, width - drawn);
    }

    public void Dispose()
    {
        // Nothing owned; the source is replaced wholesale on refresh.
    }

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
    /// Maps a <see cref="RunStyle"/> to its <see cref="Attribute"/> using the active
    /// <see cref="DiffPalette"/>. The theme-role parts are supplied by the caller:
    /// <paramref name="normal"/> is <see cref="VisualRole.Normal"/> (context foreground/background)
    /// and <paramref name="roleForeground"/> is the token's Code* foreground — so this stays a
    /// pure, headless-testable palette mapping.
    /// </summary>
    internal Attribute Map(RunStyle style, Attribute normal, Color roleForeground)
    {
        var palette = _palette();
        var foreground = style.IsGutter
            ? style.LineKind switch
            {
                DiffLineKind.Added => palette.AddedGutterForeground,
                DiffLineKind.Removed => palette.RemovedGutterForeground,
                _ => normal.Foreground,
            }
            : roleForeground;

        // A search hit wins the background so matches stand out over the diff tint.
        var background = style.SearchHit
            ? palette.SearchHitBackground
            : BackgroundFor(style.LineKind, style.Emphasis, normal.Background);
        return new Attribute(foreground, background);
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

    private Color BackgroundFor(DiffLineKind kind, bool emphasis, Color contextBackground)
    {
        var palette = _palette();
        return kind switch
        {
            DiffLineKind.Added => emphasis ? palette.AddedEmphasisBackground : palette.AddedBackground,
            DiffLineKind.Removed => emphasis ? palette.RemovedEmphasisBackground : palette.RemovedBackground,
            _ => contextBackground,
        };
    }
}
