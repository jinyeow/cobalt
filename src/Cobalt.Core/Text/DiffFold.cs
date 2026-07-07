namespace Cobalt.Core.Text;

/// <summary>
/// One row of a folded diff projection: either a real <see cref="DiffLine"/> (with its
/// original index into the unfolded list) or a fold placeholder carrying the number of
/// hidden lines and a stable <see cref="FoldId"/> used to expand it.
/// </summary>
public sealed record DiffFoldRow(int? LineIndex, DiffLine? Line, int? HiddenCount, int? FoldId)
{
    internal static DiffFoldRow ForLine(int index, DiffLine line) => new(index, line, null, null);

    internal static DiffFoldRow ForFold(int hiddenCount, int foldId) => new(null, null, hiddenCount, foldId);
}

/// <summary>
/// Immutable fold state over a unified <see cref="DiffLine"/> list (ADR 0004, no
/// Terminal.Gui types). Maximal runs of <see cref="DiffLineKind.Context"/> lines more
/// than <c>radius</c> lines away from any change collapse into a single fold marker
/// (mirrors <c>diff -U&lt;radius&gt;</c> context semantics). Expand operations return a
/// new state; the original is unaffected.
/// </summary>
public sealed class DiffFoldState
{
    private readonly IReadOnlyList<DiffLine> _lines;
    private readonly IReadOnlyList<Block> _blocks;
    private readonly IReadOnlySet<int> _expanded;

    private DiffFoldState(IReadOnlyList<DiffLine> lines, IReadOnlyList<Block> blocks, IReadOnlySet<int> expanded)
    {
        _lines = lines;
        _blocks = blocks;
        _expanded = expanded;
    }

    public static DiffFoldState Create(IReadOnlyList<DiffLine> lines, int radius = 3) =>
        new(lines, BuildBlocks(lines, radius), new HashSet<int>());

    /// <summary>Returns a new state with the given fold expanded; unknown fold ids are a no-op.</summary>
    public DiffFoldState Expand(int foldId)
    {
        var expanded = new HashSet<int>(_expanded) { foldId };
        return new DiffFoldState(_lines, _blocks, expanded);
    }

    /// <summary>Returns a new state with every fold expanded.</summary>
    public DiffFoldState ExpandAll()
    {
        var expanded = new HashSet<int>(_expanded);
        foreach (var block in _blocks)
        {
            if (block is FoldBlock fb)
            {
                expanded.Add(fb.FoldId);
            }
        }
        return new DiffFoldState(_lines, _blocks, expanded);
    }

    /// <summary>Projects the current fold state into display rows.</summary>
    public IReadOnlyList<DiffFoldRow> Rows()
    {
        var rows = new List<DiffFoldRow>();
        foreach (var block in _blocks)
        {
            switch (block)
            {
                case LineBlock lb:
                    rows.Add(DiffFoldRow.ForLine(lb.Index, _lines[lb.Index]));
                    break;
                case FoldBlock fb when _expanded.Contains(fb.FoldId):
                    for (var i = fb.StartIndex; i < fb.EndIndex; i++)
                    {
                        rows.Add(DiffFoldRow.ForLine(i, _lines[i]));
                    }
                    break;
                case FoldBlock fb:
                    rows.Add(DiffFoldRow.ForFold(fb.EndIndex - fb.StartIndex, fb.FoldId));
                    break;
            }
        }
        return rows;
    }

    private abstract record Block;

    private sealed record LineBlock(int Index) : Block;

    private sealed record FoldBlock(int FoldId, int StartIndex, int EndIndex) : Block;

    /// <summary>
    /// Splits <paramref name="lines"/> into an ordered block list: individual lines for
    /// changes and for context kept visible as radius padding around a hunk, and one
    /// fold block per maximal context run that has more than <paramref name="radius"/>
    /// lines of slack on both sides it borders (or no hunk on a side, in which case
    /// that side needs zero padding).
    /// </summary>
    private static List<Block> BuildBlocks(IReadOnlyList<DiffLine> lines, int radius)
    {
        var blocks = new List<Block>();
        var foldId = 0;
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Context)
            {
                blocks.Add(new LineBlock(i));
                i++;
                continue;
            }

            var runStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Context)
            {
                i++;
            }
            var runEnd = i;
            var runLength = runEnd - runStart;

            var leftKeep = runStart > 0 ? radius : 0;
            var rightKeep = runEnd < lines.Count ? radius : 0;

            if (runLength <= leftKeep + rightKeep)
            {
                for (var k = runStart; k < runEnd; k++)
                {
                    blocks.Add(new LineBlock(k));
                }
                continue;
            }

            for (var k = runStart; k < runStart + leftKeep; k++)
            {
                blocks.Add(new LineBlock(k));
            }

            var foldStart = runStart + leftKeep;
            var foldEnd = runEnd - rightKeep;
            blocks.Add(new FoldBlock(foldId, foldStart, foldEnd));
            foldId++;

            for (var k = foldEnd; k < runEnd; k++)
            {
                blocks.Add(new LineBlock(k));
            }
        }
        return blocks;
    }
}
