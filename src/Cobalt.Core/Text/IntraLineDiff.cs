using DiffPlex;
using DiffPlex.Chunkers;

namespace Cobalt.Core.Text;

/// <summary>
/// Word-level diff of two single lines, mapping the changed word pieces to
/// character ranges on each side (SPEC §3 review — intra-line highlighting).
/// A similarity guard suppresses spans for near-total rewrites so a replaced
/// line renders as a plain add/remove rather than confetti. Pure and line-local.
/// </summary>
public static class IntraLineDiff
{
    private static readonly Differ Differ = new();
    private static readonly WordChunker Chunker = new();
    private const double MaxChangedRatio = 0.60;
    private const int MaxLineLength = 2000;

    public static (IReadOnlyList<LineSpan> OldSpans, IReadOnlyList<LineSpan> NewSpans) Compute(
        string oldLine, string newLine)
    {
        // Length-ratio early-out: at most min(len) characters can survive as unchanged, so the
        // longer side's changed ratio is at least (longer - shorter)/longer. When that lower
        // bound already exceeds MaxChangedRatio the similarity guard is guaranteed to drop every
        // span, so skip the Myers diff entirely and return the same empty result it would.
        var longer = Math.Max(oldLine.Length, newLine.Length);
        var shorter = Math.Min(oldLine.Length, newLine.Length);
        if (longer > 0 && (double)(longer - shorter) / longer > MaxChangedRatio)
        {
            return ([], []);
        }

        return ComputeWithoutEarlyOut(oldLine, newLine);
    }

    // The full word-level compute with no length-ratio early-out. ALGO-1's early-out must
    // return exactly this method's output on every input; it is the parity reference.
    internal static (IReadOnlyList<LineSpan> OldSpans, IReadOnlyList<LineSpan> NewSpans) ComputeWithoutEarlyOut(
        string oldLine, string newLine)
    {
        if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
        {
            return ([], []);
        }

        // Length guard: Myers is ~quadratic in word count worst case on very long lines
        // (e.g. minified/generated content). Skip the compute entirely rather than let the
        // similarity guard below catch it only after paying the full cost.
        if (oldLine.Length > MaxLineLength || newLine.Length > MaxLineLength)
        {
            return ([], []);
        }

        var result = Differ.CreateDiffs(oldLine, newLine, false, false, Chunker);
        var oldOffsets = PrefixSums(result.PiecesOld);
        var newOffsets = PrefixSums(result.PiecesNew);

        var oldSpans = new List<LineSpan>();
        var newSpans = new List<LineSpan>();
        foreach (var block in result.DiffBlocks)
        {
            if (block.DeleteCountA > 0)
            {
                var start = oldOffsets[block.DeleteStartA];
                var end = oldOffsets[block.DeleteStartA + block.DeleteCountA];
                // WordChunker can emit empty pieces (e.g. on whitespace-only lines);
                // skip the resulting zero-length spans so ChangedSpans stays well-formed.
                if (end > start)
                {
                    oldSpans.Add(new LineSpan(start, end - start));
                }
            }
            if (block.InsertCountB > 0)
            {
                var start = newOffsets[block.InsertStartB];
                var end = newOffsets[block.InsertStartB + block.InsertCountB];
                if (end > start)
                {
                    newSpans.Add(new LineSpan(start, end - start));
                }
            }
        }

        var mergedOld = Merge(oldSpans);
        var mergedNew = Merge(newSpans);

        // Similarity guard: a near-total rewrite of either side is not a
        // word-level edit — drop all spans so it renders as a plain add/remove.
        if (ChangedRatio(mergedOld, oldLine.Length) > MaxChangedRatio ||
            ChangedRatio(mergedNew, newLine.Length) > MaxChangedRatio)
        {
            return ([], []);
        }

        return (mergedOld, mergedNew);
    }

    private static int[] PrefixSums(IReadOnlyList<string> pieces)
    {
        var offsets = new int[pieces.Count + 1];
        for (var i = 0; i < pieces.Count; i++)
        {
            offsets[i + 1] = offsets[i] + pieces[i].Length;
        }
        return offsets;
    }

    private static List<LineSpan> Merge(List<LineSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }
        var merged = new List<LineSpan>();
        var current = spans[0];
        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];
            if (next.Start <= current.Start + current.Length)
            {
                var end = Math.Max(current.Start + current.Length, next.Start + next.Length);
                current = new LineSpan(current.Start, end - current.Start);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }

    private static double ChangedRatio(IReadOnlyList<LineSpan> spans, int lineLength)
    {
        var changed = 0;
        foreach (var s in spans)
        {
            changed += s.Length;
        }
        if (lineLength == 0)
        {
            return changed > 0 ? 1.0 : 0.0;
        }
        return (double)changed / lineLength;
    }
}
