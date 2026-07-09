namespace Cobalt.Core.Text;

/// <summary>
/// Pure hunk/thread navigation over a unified <see cref="DiffLine"/> list (ADR 0004,
/// no Terminal.Gui types). A "hunk" is a maximal run of non-<see cref="DiffLineKind.Context"/>
/// lines; navigation always targets the hunk's first line.
/// </summary>
public static class DiffNavigator
{
    public static int NextHunk(IReadOnlyList<DiffLine> lines, int fromIndex, int count = 1) =>
        NextIndex(HunkStarts(lines), fromIndex, count);

    public static int PrevHunk(IReadOnlyList<DiffLine> lines, int fromIndex, int count = 1) =>
        PrevIndex(HunkStarts(lines), fromIndex, count);

    /// <summary>Jumps to the next index strictly after <paramref name="fromIndex"/> for which <paramref name="hasThread"/> returns true.</summary>
    public static int NextThread(IReadOnlyList<DiffLine> lines, int fromIndex, Func<int, bool> hasThread, int count = 1) =>
        NextIndex(ThreadIndices(lines, hasThread), fromIndex, count);

    public static int PrevThread(IReadOnlyList<DiffLine> lines, int fromIndex, Func<int, bool> hasThread, int count = 1) =>
        PrevIndex(ThreadIndices(lines, hasThread), fromIndex, count);

    private static List<int> HunkStarts(IReadOnlyList<DiffLine> lines)
    {
        var starts = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != DiffLineKind.Context && (i == 0 || lines[i - 1].Kind == DiffLineKind.Context))
            {
                starts.Add(i);
            }
        }
        return starts;
    }

    private static List<int> ThreadIndices(IReadOnlyList<DiffLine> lines, Func<int, bool> hasThread)
    {
        var indices = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (hasThread(i))
            {
                indices.Add(i);
            }
        }
        return indices;
    }

    /// <summary>First candidate strictly after fromIndex, advanced (count-1) more times, clamped at the end.</summary>
    private static int NextIndex(List<int> candidates, int fromIndex, int count)
    {
        var idx = candidates.FindIndex(c => c > fromIndex);
        if (idx < 0)
        {
            return fromIndex;
        }
        idx = Math.Min(idx + Math.Max(1, count) - 1, candidates.Count - 1);
        return candidates[idx];
    }

    /// <summary>Last candidate strictly before fromIndex, retreated (count-1) more times, clamped at the start.</summary>
    private static int PrevIndex(List<int> candidates, int fromIndex, int count)
    {
        var idx = candidates.FindLastIndex(c => c < fromIndex);
        if (idx < 0)
        {
            return fromIndex;
        }
        idx = Math.Max(idx - (Math.Max(1, count) - 1), 0);
        return candidates[idx];
    }
}
