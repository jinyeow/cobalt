using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

/// <summary>
/// The per-file styling cache: every diff-pane render used to re-tokenize and recompose the whole
/// file, so a single keypress (e/E, s, T, a comment round-trip) paid O(lines × line length). These
/// guard that a composition survives across renders — asserted by reference identity, since that is
/// exactly what "did not recompose" means — and that it is dropped when, and only when, one of its
/// inputs actually changed.
/// </summary>
public class DiffStyleCacheTests
{
    private static readonly IReadOnlyList<DiffLine> Sample =
    [
        new(DiffLineKind.Added, null, 1, "var x = 1;"),
        new(DiffLineKind.Context, 1, 2, "return x;"),
        new(DiffLineKind.Removed, 2, null, "var y = 2;"),
    ];

    private static IReadOnlySet<int> None => new HashSet<int>();

    private static IReadOnlySet<int> Lines(params int[] numbers) => new HashSet<int>(numbers);

    /// <summary>A cache pointed at <see cref="Sample"/> with no commented lines, as the first render leaves it.</summary>
    private static DiffStyleCache Prepared()
    {
        var cache = new DiffStyleCache();
        cache.Prepare(Sample, Language.CSharp, None, None);
        return cache;
    }

    [Fact]
    public void Unified_Composes_A_Line_Exactly_As_The_Styler_Does()
    {
        var expected = DiffLineStyler.Compose(
            Sample[0], SyntaxTokenizer.Tokenize(Sample[0].Text, Language.CSharp), hasThread: false);

        var actual = Prepared().Unified(0);

        Assert.Equal(expected.DisplayText, actual.DisplayText);
        Assert.Equal(expected.Runs, actual.Runs);
    }

    [Fact]
    public void Unified_Reuses_The_Composition_Across_Renders()
    {
        var cache = Prepared();
        var first = cache.Unified(1);

        cache.Prepare(Sample, Language.CSharp, None, None); // the next render
        var second = cache.Unified(1);

        Assert.Same(first, second);
    }

    [Fact]
    public void Unified_Recomposes_Only_The_Line_Whose_Thread_Marker_Changed()
    {
        var cache = Prepared();
        var commented = cache.Unified(0); // added line, new line number 1
        var untouched = cache.Unified(1);

        cache.Prepare(Sample, Language.CSharp, None, Lines(1)); // a comment landed on the added line

        Assert.NotSame(commented, cache.Unified(0));
        Assert.StartsWith("●", cache.Unified(0).DisplayText);
        Assert.Same(untouched, cache.Unified(1));
    }

    [Fact]
    public void Unified_Anchors_A_Removed_Line_To_The_Left_Side()
    {
        var cache = Prepared();
        var before = cache.Unified(2); // removed line, old line number 2

        cache.Prepare(Sample, Language.CSharp, Lines(2), None);

        Assert.NotSame(before, cache.Unified(2));
        Assert.StartsWith("●", cache.Unified(2).DisplayText);
    }

    [Fact]
    public void Unified_Keeps_The_Cache_When_The_Thread_Sets_Are_Rebuilt_Unchanged()
    {
        // The view rebuilds the sets on every render, so a fresh instance carrying the same
        // line numbers must not count as a change.
        var cache = new DiffStyleCache();
        cache.Prepare(Sample, Language.CSharp, None, Lines(1));
        var first = cache.Unified(0);

        cache.Prepare(Sample, Language.CSharp, None, Lines(1));

        Assert.Same(first, cache.Unified(0));
    }

    [Fact]
    public void Unified_Drops_The_Cache_When_The_File_Changes()
    {
        var cache = Prepared();
        var first = cache.Unified(0);

        cache.Prepare([.. Sample], Language.CSharp, None, None); // a different file's lines

        Assert.NotSame(first, cache.Unified(0));
    }

    [Fact]
    public void Unified_Drops_The_Cache_When_The_Language_Changes()
    {
        var cache = Prepared();
        var first = cache.Unified(0);

        cache.Prepare(Sample, Language.Python, None, None);

        Assert.NotSame(first, cache.Unified(0));
    }

    [Fact]
    public void Overlaying_Search_Hits_Leaves_The_Cached_Line_Unmarked()
    {
        // The view overlays search hits onto the cached composition. The cache must never see
        // them: a query is per-render state, so a hit baked into a cached line would outlive the
        // search and freeze stale highlights into every later render of that file. This holds by
        // construction today — WithSearchHits builds a new run list — so this guards a future
        // in-place "optimisation": StyledLine.Runs is a mutable List behind an IReadOnlyList, and
        // nothing else would catch one.
        var cache = Prepared();
        var line = cache.Unified(0);

        var highlighted = DiffLineStyler.WithSearchHits(line, [new LineSpan(0, 3)], line.Runs[0].Length);

        Assert.Contains(highlighted.Runs, r => r.Style.SearchHit); // the overlay really did mark it
        Assert.Same(line, cache.Unified(0));
        Assert.DoesNotContain(cache.Unified(0).Runs, r => r.Style.SearchHit);
    }

    [Fact]
    public void SideBySide_Composes_Rows_Exactly_As_The_Composer_Does()
    {
        var rows = SideBySideComposer.Pair(Sample);
        var expected = SideBySideComposer.Compose(Sample, rows, Language.CSharp, _ => false, 40);

        var actual = Prepared().SideBySide(rows, 40);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Select(l => l.DisplayText), actual.Select(l => l.DisplayText));
        Assert.Equal(expected[0].Runs, actual[0].Runs);
    }

    [Fact]
    public void SideBySide_Reuses_The_Composition_Across_Renders()
    {
        var rows = SideBySideComposer.Pair(Sample);
        var cache = Prepared();
        var first = cache.SideBySide(rows, 40);

        cache.Prepare(Sample, Language.CSharp, None, None);

        Assert.Same(first, cache.SideBySide(SideBySideComposer.Pair(Sample), 40));
    }

    [Fact]
    public void SideBySide_Recomposes_When_The_Column_Width_Changes()
    {
        var rows = SideBySideComposer.Pair(Sample);
        var cache = Prepared();
        var first = cache.SideBySide(rows, 40);

        Assert.NotSame(first, cache.SideBySide(rows, 30));
    }

    [Fact]
    public void SideBySide_Recomposes_When_A_Thread_Marker_Changes()
    {
        var rows = SideBySideComposer.Pair(Sample);
        var cache = Prepared();
        var first = cache.SideBySide(rows, 40);

        cache.Prepare(Sample, Language.CSharp, None, Lines(1));

        Assert.NotSame(first, cache.SideBySide(rows, 40));
        // The marker sits in the right column's gutter, not at the start of the row.
        Assert.Contains(cache.SideBySide(rows, 40), l => l.DisplayText.Contains('●'));
    }

    [Fact]
    public void The_Two_Modes_Keep_Separate_Caches_So_Toggling_Costs_Nothing()
    {
        // s flips between unified and side-by-side; each mode's composition stays valid, so
        // flipping back does not recompose the file.
        var rows = SideBySideComposer.Pair(Sample);
        var cache = Prepared();
        var unified = cache.Unified(0);
        var split = cache.SideBySide(rows, 40);

        Assert.Same(unified, cache.Unified(0));
        Assert.Same(split, cache.SideBySide(rows, 40));
    }
}
