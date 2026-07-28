namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The shared incremental-filter matcher: an order-preserving, case-insensitive subsequence
/// test plus the prefix-first ranking the `:` palette completes with. Extracted from
/// <see cref="PaletteSuggestionsViewModel"/> so the menu component (ADR 0022 stage C) filters
/// rows with the same matcher the palette ranks commands with. Pure (ADR 0004).
/// </summary>
internal static class FuzzyFilter
{
    /// <summary>
    /// The pool narrowed to the entries <paramref name="query"/> matches, prefix matches first
    /// and each group in pool order. An empty query returns the pool unchanged.
    /// </summary>
    internal static IReadOnlyList<string> Rank(IReadOnlyList<string> pool, string query)
    {
        if (query.Length == 0)
        {
            return pool;
        }
        var prefixMatches = pool.Where(p => p.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var fuzzyMatches = pool
            .Where(p => !prefixMatches.Contains(p) && IsSubsequence(query, p))
            .ToList();
        return [.. prefixMatches, .. fuzzyMatches];
    }

    /// <summary>Whether every character of <paramref name="query"/> appears in
    /// <paramref name="candidate"/> in order (gaps allowed), ignoring case.</summary>
    internal static bool IsSubsequence(string query, string candidate)
    {
        var queryIndex = 0;
        foreach (var ch in candidate)
        {
            if (queryIndex < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[queryIndex]))
            {
                queryIndex++;
            }
        }
        return queryIndex == query.Length;
    }
}
