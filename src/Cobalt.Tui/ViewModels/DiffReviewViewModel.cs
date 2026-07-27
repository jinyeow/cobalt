using Cobalt.Core.Text;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// What the diff dialog decides, minus the widgets: the state and pure transitions behind the
/// review pane, unit-testable without a terminal (ADR 0004). Dialog-owned, like
/// <see cref="DiffPaneComposer"/> — the screen constructs one and keeps it, so no test's dialog
/// construction changes.
///
/// <para><b>Identity — the rule this class is built around.</b> The file under the tree cursor and
/// the file on screen are different things during a select: <c>PrDiffViewModel.SelectFileAsync</c>
/// moves <c>SelectedFile</c> synchronously and only publishes the fetched diff when it lands, so
/// in that window the cursor has already moved on while the previous file is still displayed. Four
/// shipped bugs came from confusing them. Everything this class owns is <b>DISPLAYED</b>-side —
/// derived from <c>CurrentDiff</c> / <c>CurrentDiffSnapshot</c> — and it must never read
/// <c>SelectedFile</c>. The <b>CURSOR</b> half stays in the dialog, which owns the async select
/// that writes it.</para>
/// </summary>
internal sealed class DiffReviewViewModel(PrDiffViewModel vm)
{
    private string? _searchQuery;
    private IReadOnlyList<(int LineIndex, LineSpan Span)> _searchMatches = [];
    private int _searchIndex;

    /// <summary>The active search's matches, as unified line indexes into the DISPLAYED diff.</summary>
    internal IReadOnlyList<(int LineIndex, LineSpan Span)> Matches => _searchMatches;

    /// <summary>The number of matches for the active search (0 when no search is active).</summary>
    internal int MatchCount => _searchMatches.Count;

    /// <summary>
    /// The result of applying a search: at most one of the two is set. <paramref name="JumpToLine"/>
    /// is the unified line the caller should reveal and select; <paramref name="NoMatchesFor"/> is
    /// the (trimmed) query the caller should report as unmatched. Both null means the search was
    /// cleared, which is silent. They are separate fields because "no matches" must not be
    /// expressible as a jump to line 0 — that would quietly move the cursor to the top of the file.
    /// </summary>
    internal readonly record struct SearchOutcome(int? JumpToLine, string? NoMatchesFor);

    /// <summary>
    /// Runs a query against the DISPLAYED diff, parking the match index on the first hit. A blank
    /// or whitespace-only query — or no diff on screen — clears the search instead.
    /// </summary>
    internal SearchOutcome ApplySearch(string? query)
    {
        if (vm.CurrentDiff is not { } diff || string.IsNullOrWhiteSpace(query))
        {
            ClearSearch();
            return new SearchOutcome(null, null);
        }
        _searchQuery = query.Trim();
        _searchMatches = DiffSearch.Find(diff.Lines, _searchQuery);
        _searchIndex = 0;
        return _searchMatches.Count > 0
            ? new SearchOutcome(_searchMatches[_searchIndex].LineIndex, null)
            : new SearchOutcome(null, _searchQuery);
    }

    /// <summary>
    /// n/N: advance to the next/previous match, wrapping. Returns the unified line to reveal, or
    /// null when there is no active search — so the caller stays put rather than selecting line 0.
    /// </summary>
    internal int? StepSearch(bool forward)
    {
        if (_searchMatches.Count == 0)
        {
            return null;
        }
        _searchIndex = forward
            ? DiffSearch.Next(_searchMatches, _searchIndex)
            : DiffSearch.Prev(_searchMatches, _searchIndex);
        return _searchMatches[_searchIndex].LineIndex;
    }

    /// <summary>
    /// Drops the active search. Called when the query is blank and when the DISPLAYED file changes:
    /// matches are line-index scoped to one file, so carrying them across would decorate the new
    /// file at the old file's lines.
    /// </summary>
    internal void ClearSearch()
    {
        _searchQuery = null;
        _searchMatches = [];
    }
}
