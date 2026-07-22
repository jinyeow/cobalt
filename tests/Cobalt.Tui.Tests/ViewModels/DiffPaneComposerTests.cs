using Cobalt.Core.Text;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// Pure unit tests for <see cref="DiffPaneComposer"/> (H1): the diff-pane composition extracted
/// from <c>DiffReviewDialog.Render</c>, exercised with no Terminal.Gui — a <see cref="FileDiff"/>
/// built straight from <see cref="DiffService.Unified"/>. Covers fold-state creation vs reuse, the
/// side-by-side pairing and column-width math, the row map, the search-hit overlay, anchorless fold
/// markers, and determinism.
/// </summary>
public class DiffPaneComposerTests
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    // A change at the top then 14 identical context lines: the trailing context run (radius 3 keeps
    // 3, no right side) folds, so the unified projection has exactly one fold to expand.
    private static FileDiff FoldingDiff()
    {
        var ctx = string.Join("\n", Enumerable.Range(0, 14).Select(i => $"ctx {i}"));
        return DiffService.Unified("old\n" + ctx + "\n", "new\n" + ctx + "\n");
    }

    private static DiffPaneRequest Req(
        FileDiff diff,
        bool sideBySide,
        DiffFoldState? foldState = null,
        IReadOnlyList<(int LineIndex, LineSpan Span)>? search = null,
        int contentWidth = 120) =>
        new(diff, "/a.cs", sideBySide, foldState, search ?? [], contentWidth, None, None);

    [Fact]
    public void Compose_Unified_Creates_Fold_State_When_Null()
    {
        var composition = new DiffPaneComposer().Compose(Req(FoldingDiff(), sideBySide: false));

        Assert.NotNull(composition.FoldState);
        Assert.Contains(composition.Rows, r => r.FoldId is not null); // the distant context folded
    }

    [Fact]
    public void Compose_Unified_Reuses_Passed_Fold_State()
    {
        var diff = FoldingDiff();
        var composer = new DiffPaneComposer();
        var initial = composer.Compose(Req(diff, sideBySide: false));
        var foldId = initial.Rows.First(r => r.FoldId is not null).FoldId!.Value;

        // Expand the fold and feed that state back in: the composer must reuse it, not rebuild fresh.
        var expanded = initial.FoldState!.Expand(foldId);
        var reused = composer.Compose(Req(diff, sideBySide: false, foldState: expanded));

        Assert.Same(expanded, reused.FoldState);
        Assert.DoesNotContain(reused.Rows, r => r.FoldId is not null); // expansion is reflected
        Assert.True(reused.Rows.Count > initial.Rows.Count);
    }

    [Fact]
    public void Compose_SideBySide_Returns_Null_Fold_State_And_Paired_Rows()
    {
        var diff = DiffService.Unified("keep\nold\ntail\n", "keep\nnew\ntail\n");

        var composition = new DiffPaneComposer().Compose(Req(diff, sideBySide: true));

        Assert.Null(composition.FoldState); // side-by-side shows full context, carries no folds
        Assert.All(composition.Rows, r => Assert.Null(r.LineIndex)); // side-by-side rows, not unified
        Assert.All(composition.Rows, r => Assert.Null(r.FoldId));
        Assert.Contains(composition.Rows, r => r.LeftIndex is not null && r.RightIndex is not null);
    }

    [Fact]
    public void Compose_SideBySide_Column_Width_Is_Half_Content_Minus_Separator()
    {
        var diff = DiffService.Unified("a\n", "b\n");
        var sep = SideBySideComposer.Separator.Length;

        var wide = new DiffPaneComposer().Compose(Req(diff, sideBySide: true, contentWidth: 81));
        var expectedWidth = (81 - sep) / 2;
        Assert.All(wide.Styled, l => Assert.Equal(2 * expectedWidth + sep, l.DisplayText.Length));

        // ContentWidth 1 can't fit two columns; the width clamps to 1 rather than going non-positive.
        var narrow = new DiffPaneComposer().Compose(Req(diff, sideBySide: true, contentWidth: 1));
        Assert.All(narrow.Styled, l => Assert.Equal(2 * 1 + sep, l.DisplayText.Length));
    }

    [Fact]
    public void Compose_RowMap_Keys_On_Every_Side_First_Row_Wins()
    {
        // A hand-built row list: line 5 appears as a unified row (row 0) and again as a side-by-side
        // right index (row 1). The map keys on every side but the first row to claim an index wins.
        DiffRow[] rows =
        [
            new(LineIndex: 5, LeftIndex: null, RightIndex: null, FoldId: null, HiddenCount: null),
            new(LineIndex: null, LeftIndex: 2, RightIndex: 5, FoldId: null, HiddenCount: null),
            new(LineIndex: null, LeftIndex: null, RightIndex: null, FoldId: 0, HiddenCount: 8),
        ];

        var map = DiffPaneComposer.BuildLineToRow(rows);

        Assert.Equal(0, map[5]);              // row 0 claimed 5 first; row 1's right index does not win
        Assert.Equal(1, map[2]);              // left index keyed too
        Assert.Equal(2, map.Count);           // only lines 5 and 2 — the fold-marker row contributes no key
    }

    [Fact]
    public void Compose_Search_Hits_Overlay_Only_Matched_Lines()
    {
        // All-added file (no folds): "needle" matches line 1 only.
        var diff = DiffService.Unified("", "alpha\nneedle\nbeta\n");
        var composer = new DiffPaneComposer();

        var plain = composer.Compose(Req(diff, sideBySide: false));
        var beforeByLine = LinesByRow(plain);

        var matches = DiffSearch.Find(diff.Lines, "needle");
        var searched = composer.Compose(Req(diff, sideBySide: false, search: matches));
        var afterByLine = LinesByRow(searched);

        for (var line = 0; line < diff.Lines.Count; line++)
        {
            if (line == matches[0].LineIndex)
            {
                Assert.Contains(afterByLine[line].Runs, r => r.Style.SearchHit); // decorated
            }
            else
            {
                Assert.Same(beforeByLine[line], afterByLine[line]); // untouched — overlay, not recompose
                Assert.DoesNotContain(afterByLine[line].Runs, r => r.Style.SearchHit);
            }
        }
    }

    [Fact]
    public void Compose_Fold_Marker_Row_Is_Anchorless()
    {
        var composition = new DiffPaneComposer().Compose(Req(FoldingDiff(), sideBySide: false));

        var marker = composition.Rows.First(r => r.FoldId is not null);
        Assert.Null(marker.Anchor);     // a fold marker anchors to no unified line
        Assert.Null(marker.LineIndex);
        Assert.NotNull(marker.HiddenCount);
    }

    [Fact]
    public void Compose_Is_Deterministic_For_Same_Request()
    {
        var diff = FoldingDiff();
        var request = Req(diff, sideBySide: false);

        var a = new DiffPaneComposer().Compose(request);
        var b = new DiffPaneComposer().Compose(request);

        Assert.Equal(a.Rows, b.Rows); // DiffRow is a value record — sequence equality
        Assert.Equal(
            a.Styled.Select(l => l.DisplayText),
            b.Styled.Select(l => l.DisplayText));
        Assert.Equal(a.LineToRow.OrderBy(kv => kv.Key), b.LineToRow.OrderBy(kv => kv.Key));
    }

    /// <summary>The composed line shown for each visible unified line index.</summary>
    private static Dictionary<int, StyledLine> LinesByRow(DiffPaneComposition composition)
    {
        var map = new Dictionary<int, StyledLine>();
        for (var i = 0; i < composition.Rows.Count; i++)
        {
            if (composition.Rows[i].LineIndex is { } line)
            {
                map[line] = composition.Styled[i];
            }
        }
        return map;
    }
}
