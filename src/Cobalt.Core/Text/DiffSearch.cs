namespace Cobalt.Core.Text;

/// <summary>
/// Pure case-insensitive substring search over a unified <see cref="DiffLine"/> list
/// (ADR 0004, no Terminal.Gui types).
/// </summary>
public static class DiffSearch
{
    /// <summary>
    /// Every case-insensitive occurrence of <paramref name="query"/> across
    /// <paramref name="lines"/>, ordered by line then by position within the line.
    /// </summary>
    public static IReadOnlyList<(int LineIndex, LineSpan Span)> Find(IReadOnlyList<DiffLine> lines, string query)
    {
        var results = new List<(int LineIndex, LineSpan Span)>();
        if (string.IsNullOrEmpty(query))
        {
            return results;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            var start = 0;
            while (start <= text.Length - query.Length)
            {
                var idx = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }
                results.Add((i, new LineSpan(idx, query.Length)));
                start = idx + query.Length;
            }
        }
        return results;
    }

    /// <summary>
    /// The index (into <paramref name="matches"/>, not into the diff's lines) of the
    /// match after <paramref name="currentIndex"/>, wrapping to the first match past
    /// the last. Returns <paramref name="currentIndex"/> unchanged when there are no matches.
    /// </summary>
    public static int Next(IReadOnlyList<(int LineIndex, LineSpan Span)> matches, int currentIndex)
    {
        if (matches.Count == 0)
        {
            return currentIndex;
        }
        return ((currentIndex + 1) % matches.Count + matches.Count) % matches.Count;
    }

    /// <summary>
    /// The index (into <paramref name="matches"/>, not into the diff's lines) of the
    /// match before <paramref name="currentIndex"/>, wrapping to the last match before
    /// the first. Returns <paramref name="currentIndex"/> unchanged when there are no matches.
    /// </summary>
    public static int Prev(IReadOnlyList<(int LineIndex, LineSpan Span)> matches, int currentIndex)
    {
        if (matches.Count == 0)
        {
            return currentIndex;
        }
        return ((currentIndex - 1) % matches.Count + matches.Count) % matches.Count;
    }
}
