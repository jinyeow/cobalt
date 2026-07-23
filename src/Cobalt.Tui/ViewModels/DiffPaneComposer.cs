using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// One visible diff-pane row mapped back to the original unified <see cref="DiffLine"/> list.
/// The single map every consumer (comment/thread anchoring, hunk/thread/search navigation)
/// resolves through, so anchoring stays correct across unified, folded, and side-by-side modes.
/// A unified line sets <see cref="LineIndex"/>; a side-by-side row sets <see cref="LeftIndex"/>/
/// <see cref="RightIndex"/>; a fold marker sets <see cref="FoldId"/>/<see cref="HiddenCount"/>
/// and is anchorless.
/// </summary>
internal sealed record DiffRow(int? LineIndex, int? LeftIndex, int? RightIndex, int? FoldId, int? HiddenCount)
{
    /// <summary>The unified diff-line this row anchors to (new/right side preferred); null for a fold marker.</summary>
    public int? Anchor => LineIndex ?? RightIndex ?? LeftIndex;
}

/// <summary>
/// The inputs one diff-pane composition needs. <paramref name="SideBySide"/> is the
/// <em>effective</em> mode — the dialog has already forced unified at narrow widths, so this is
/// taken as final. <paramref name="FoldState"/> null means compose a fresh
/// <see cref="DiffFoldState"/> from the diff's lines; it is ignored in side-by-side (which shows
/// full context). Commented left/right line numbers feed the thread markers.
/// </summary>
internal sealed record DiffPaneRequest(
    FileDiff Diff,
    string Path,
    bool SideBySide,
    DiffFoldState? FoldState,
    IReadOnlyList<(int LineIndex, LineSpan Span)> SearchMatches,
    int ContentWidth,
    IReadOnlySet<int> CommentedLeft,
    IReadOnlySet<int> CommentedRight);

/// <summary>
/// The result of one composition: the visible rows, the styled lines the diff pane draws (index
/// aligned with <see cref="Rows"/>), the unified line → first row map, and the fold state the
/// dialog should retain (the freshly created one in unified mode, or null in side-by-side).
/// </summary>
internal sealed record DiffPaneComposition(
    IReadOnlyList<DiffRow> Rows,
    IReadOnlyList<StyledLine> Styled,
    IReadOnlyDictionary<int, int> LineToRow,
    DiffFoldState? FoldState);

/// <summary>
/// Pure diff-pane compositor (ADR 0004: no Terminal.Gui types) extracted from
/// <c>DiffReviewDialog.Render</c> (H1). Turns a <see cref="DiffPaneRequest"/> into the rows, styled
/// lines, and row map the pane binds — the unified fold projection with the search-hit overlay, or
/// the side-by-side pairing — with none of the dialog's mode/fold/search branching left in the
/// view. One instance per dialog, mirroring the single <see cref="DiffStyleCache"/> per dialog: the
/// cache keys on the <see cref="FileDiff.Lines"/> reference, so a file's compositions are reused
/// across renders that change no line (fold expand, filter, a landed comment).
/// </summary>
internal sealed class DiffPaneComposer
{
    private readonly DiffStyleCache _styleCache = new();

    public DiffPaneComposition Compose(DiffPaneRequest request)
    {
        var diff = request.Diff;
        // Point the style cache at this render's inputs: it reuses every composition whose line,
        // language and thread marker are unchanged, so a render that only expands a fold, filters
        // the tree or lands a comment no longer re-tokenizes the whole file. The commented lines
        // are looked up once by the caller rather than scanned per line.
        var language = LanguageDetector.FromPath(request.Path);
        _styleCache.Prepare(diff.Lines, language, request.CommentedLeft, request.CommentedRight);

        var rows = new List<DiffRow>();
        List<StyledLine> styled;
        DiffFoldState? foldState;

        if (request.SideBySide)
        {
            // Side-by-side shows full context (no fold), so it carries no fold state.
            foldState = null;
            var sbs = SideBySideComposer.Pair(diff.Lines);
            foreach (var r in sbs)
            {
                rows.Add(new DiffRow(null, r.LeftIndex, r.RightIndex, null, null));
            }
            var columnWidth = Math.Max(1, (request.ContentWidth - SideBySideComposer.Separator.Length) / 2);
            styled = [.. _styleCache.SideBySide(sbs, columnWidth)];
        }
        else
        {
            // Unified folds distant context (radius 3) by default; e/E expand. The caller passes the
            // retained fold state on a same-file refresh so expansions survive, or null to rebuild.
            foldState = request.FoldState ?? DiffFoldState.Create(diff.Lines);
            var hitsByLine = request.SearchMatches.Count == 0
                ? null
                : request.SearchMatches.GroupBy(m => m.LineIndex)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<LineSpan>)[.. g.Select(m => m.Span)]);
            styled = [];
            foreach (var foldRow in foldState.Rows())
            {
                if (foldRow.Line is not null && foldRow.LineIndex is { } li)
                {
                    rows.Add(new DiffRow(li, null, null, null, null));
                    // Search hits stay an overlay on the cached composition: a query change must not
                    // invalidate a line's styling, only decorate it.
                    var composed = _styleCache.Unified(li);
                    if (hitsByLine is not null && hitsByLine.TryGetValue(li, out var spans))
                    {
                        composed = DiffLineStyler.WithSearchHits(composed, spans, composed.Runs[0].Length);
                    }
                    styled.Add(composed);
                }
                else
                {
                    rows.Add(new DiffRow(null, null, null, foldRow.FoldId, foldRow.HiddenCount));
                    styled.Add(FoldMarkerLine(foldRow.HiddenCount ?? 0));
                }
            }
        }

        return new DiffPaneComposition(rows, styled, BuildLineToRow(rows), foldState);
    }

    /// <summary>
    /// The unified line index → first row showing it, keyed on every side a row exposes
    /// (unified <see cref="DiffRow.LineIndex"/>, or side-by-side left/right). First row wins, so a
    /// lookup matches the same row the old first-match scan did. Rebuilt with the rows so
    /// IsLineVisible / SelectDiffLine are O(1) rather than an O(rows) scan on every nav (RENDER-7).
    /// </summary>
    internal static Dictionary<int, int> BuildLineToRow(IReadOnlyList<DiffRow> rows)
    {
        var map = new Dictionary<int, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.LineIndex is { } li)
            {
                map.TryAdd(li, i);
            }
            if (row.LeftIndex is { } le)
            {
                map.TryAdd(le, i);
            }
            if (row.RightIndex is { } ri)
            {
                map.TryAdd(ri, i);
            }
        }
        return map;
    }

    /// <summary>A dim placeholder row standing in for a run of hidden context lines.</summary>
    internal static StyledLine FoldMarkerLine(int hidden)
    {
        var text = $"    ··· {hidden} lines ···";
        return new StyledLine(
            text,
            [new StyledRun(0, text.Length, new RunStyle(TokenKind.Comment, DiffLineKind.Context, Emphasis: false, IsGutter: false))]);
    }
}
