using Cobalt.Tui.Input;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Tab-completion / fuzzy-filter state for the `:` command palette. Pure (ADR 0004) — the
/// shell renders <see cref="Current"/> inline and calls <see cref="Accept"/> on Tab.
/// Completes command names from <see cref="PaletteCommandParser.Catalog"/>, and the argument
/// for commands whose <see cref="PaletteArgKind"/> has a provider (<c>:context</c>,
/// <c>:project</c>) against the real names the ctor functions return.
/// </summary>
public sealed class PaletteSuggestionsViewModel(
    Func<IReadOnlyList<string>> contexts, Func<IReadOnlyList<string>> projects)
{
    private string _rawInput = "";
    private string _leadingColons = "";
    private IReadOnlyList<string> _candidates = [];
    private int _index = -1;
    private bool _completingArgument;
    private string _acceptedPrefix = "";

    public string? Current => _index >= 0 && _index < _candidates.Count ? _candidates[_index] : null;

    public void SetInput(string input)
    {
        _rawInput = input;
        var trimmed = input.TrimStart(':');
        _leadingColons = input[..(input.Length - trimmed.Length)];
        var spaceIndex = trimmed.IndexOf(' ');

        if (spaceIndex < 0)
        {
            _completingArgument = false;
            _acceptedPrefix = "";
            _candidates = Rank(PaletteCommandParser.Catalog.Select(e => e.Name).ToList(), trimmed);
        }
        else
        {
            // Match PaletteCommandParser.Parse's TrimEntries split: extra whitespace around
            // either half (e.g. "context  w") must not break completion.
            var commandWord = trimmed[..spaceIndex].Trim();
            var argumentText = trimmed[(spaceIndex + 1)..].Trim();
            var entry = PaletteCommandParser.Catalog.FirstOrDefault(
                e => e.Name == commandWord || e.Aliases.Contains(commandWord));
            var provider = entry.Name is null ? null : ArgumentProvider(entry.ArgKind);

            if (provider is null)
            {
                _completingArgument = false;
                _acceptedPrefix = "";
                _candidates = [];
            }
            else
            {
                _completingArgument = true;
                _acceptedPrefix = $"{entry.Name} ";
                _candidates = Rank(provider(), argumentText);
            }
        }

        _index = _candidates.Count > 0 ? 0 : -1;
    }

    public void CycleNext()
    {
        if (_candidates.Count == 0)
        {
            return;
        }
        _index = (_index + 1) % _candidates.Count;
    }

    public void CyclePrev()
    {
        if (_candidates.Count == 0)
        {
            return;
        }
        _index = (_index - 1 + _candidates.Count) % _candidates.Count;
    }

    /// <summary>
    /// Returns the completed input text, preserving the raw input's exact leading-colon
    /// prefix (typed `:` is tolerated the same way <see cref="PaletteCommandParser.Parse"/>
    /// tolerates it). Argument completion always yields "&lt;command&gt; &lt;value&gt;";
    /// top-level command completion adds a trailing space only when that command takes a
    /// provider-backed argument (so Tab can keep completing). Leaves the raw input unchanged
    /// when there is no candidate.
    /// </summary>
    public string Accept()
    {
        var current = Current;
        if (current is null)
        {
            return _rawInput;
        }
        if (_completingArgument)
        {
            return _leadingColons + _acceptedPrefix + current;
        }
        var entry = PaletteCommandParser.Catalog.First(e => e.Name == current);
        return entry.ArgKind == PaletteArgKind.None
            ? _leadingColons + current
            : $"{_leadingColons}{current} ";
    }

    private Func<IReadOnlyList<string>>? ArgumentProvider(PaletteArgKind argKind) => argKind switch
    {
        PaletteArgKind.Context => contexts,
        PaletteArgKind.Project => projects,
        _ => null,
    };

    private static IReadOnlyList<string> Rank(IReadOnlyList<string> pool, string query)
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

    private static bool IsSubsequence(string query, string candidate)
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
