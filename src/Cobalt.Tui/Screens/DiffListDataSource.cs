using System.Collections;
using System.Collections.Specialized;
using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;
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
/// </summary>
public sealed class DiffListDataSource(IReadOnlyList<StyledLine> lines) : IListDataSource
{
    private readonly IReadOnlyList<StyledLine> _lines = lines;

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

        var drawn = 0;
        foreach (var runItem in styled.Runs)
        {
            var start = Math.Max(runItem.Start, viewportX);
            var end = Math.Min(runItem.Start + runItem.Length, viewportX + width);
            if (end <= start)
            {
                continue;
            }
            listView.SetAttribute(Map(listView, runItem.Style));
            listView.AddStr(styled.DisplayText.Substring(start, end - start));
            drawn += end - start;
        }

        // Full-width diff tint: pad the rest of the row in the line-kind background.
        var normal = listView.GetAttributeForRole(VisualRole.Normal);
        listView.SetAttribute(new Attribute(normal.Foreground, BackgroundFor(listView, lineKind, emphasis: false)));
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

    private static Attribute Map(ListView listView, RunStyle style)
    {
        Color foreground;
        if (style.IsGutter)
        {
            foreground = style.LineKind switch
            {
                DiffLineKind.Added => new Color(ColorName16.BrightGreen),
                DiffLineKind.Removed => new Color(ColorName16.BrightRed),
                _ => listView.GetAttributeForRole(VisualRole.Normal).Foreground,
            };
        }
        else
        {
            foreground = listView.GetAttributeForRole(RoleFor(style.Token)).Foreground;
        }

        var background = BackgroundFor(listView, style.LineKind, style.Emphasis);
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

    private static Color BackgroundFor(ListView listView, DiffLineKind kind, bool emphasis) => kind switch
    {
        DiffLineKind.Added => new Color(emphasis ? "#1e6b1e" : "#123a12"),
        DiffLineKind.Removed => new Color(emphasis ? "#6b2020" : "#3a1212"),
        _ => listView.GetAttributeForRole(VisualRole.Normal).Background,
    };
}
