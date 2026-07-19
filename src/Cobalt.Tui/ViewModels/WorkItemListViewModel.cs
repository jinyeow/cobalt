using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.ViewModels;

/// <summary>Abstraction over WorkItemsApi so the view-model is testable without HTTP.</summary>
public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct);
}

/// <summary>State for the "my work items" list: async load, error surface, live filter, selection.</summary>
public sealed class WorkItemListViewModel(IWorkItemSource source, bool includeCompleted = false, string? projectFilter = null)
{
    private IReadOnlyList<WorkItem> _all = [];
    private string _filter = "";
    private int _selectedIndex;
    private bool _includeCompleted = includeCompleted;
    private string? _projectFilter = string.IsNullOrEmpty(projectFilter) ? null : projectFilter;
    private CancellationToken _reloadToken = CancellationToken.None;
    private int _loadSeq;

    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<WorkItem> Rows { get; private set; } = [];

    public event Action? Changed;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            ApplyFilter();
        }
    }

    /// <summary>Server-side: false hides completed states (the default), true shows them.</summary>
    public bool IncludeCompleted
    {
        get => _includeCompleted;
        set
        {
            if (_includeCompleted == value)
            {
                return;
            }
            _includeCompleted = value;
            Reload();
        }
    }

    /// <summary>Server-side single-project narrowing (WIQL <c>[System.TeamProject]</c>); null clears it.</summary>
    public string? ProjectFilter
    {
        get => _projectFilter;
        set
        {
            var next = string.IsNullOrEmpty(value) ? null : value;
            if (string.Equals(_projectFilter, next, StringComparison.Ordinal))
            {
                return;
            }
            _projectFilter = next;
            Reload();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = Math.Clamp(value, 0, Math.Max(0, Rows.Count - 1));
    }

    public WorkItem? Selected =>
        Rows.Count == 0 ? null : Rows[Math.Clamp(_selectedIndex, 0, Rows.Count - 1)];

    /// <summary>
    /// Guidance for an empty list — non-null only once loading/error are ruled out, so it never
    /// flickers over a transient state. The client-side substring filter or the server-side
    /// project filter narrowing to zero names itself and how to clear it; otherwise the list is
    /// genuinely empty ("assigned to you" is inherently narrow), so the message points at the
    /// two knobs most likely to widen it: showing completed items and broadening the org scope.
    /// </summary>
    public string? EmptyStateText
    {
        get
        {
            if (IsLoading || Error is not null || Rows.Count != 0)
            {
                return null;
            }

            if (_filter.Length != 0)
            {
                return $"0 of {_all.Count} work items match \"{_filter}\" — clear the search (Esc) to see them all.";
            }

            if (_projectFilter is not null)
            {
                return $"No work items in project \"{_projectFilter}\" — clear with :project (no argument).";
            }

            return "No work items assigned to you — :done show includes completed states; :scope org widens the query.";
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        // Remember the token so a filter setter can reload on its own later.
        _reloadToken = ct;
        // Stamp this load; only the newest may commit its results, so `:done show` (slow)
        // then `:done hide` (fast) can't leave the slow result overwriting the fast one (M1).
        var seq = ++_loadSeq;
        IsLoading = true;
        Error = null;
        Changed?.Invoke();

        IReadOnlyList<WorkItem> result;
        string? error = null;
        try
        {
            var query = new WorkItemQuery(_includeCompleted, _projectFilter);
            result = await source.QueryMyWorkItemsAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a permanent "loading…" pane (LOW-1).
            error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
            result = [];
        }

        // A newer load superseded this one while it was in flight; drop its results
        // so it cannot clobber the current filter state.
        if (seq != _loadSeq)
        {
            return;
        }

        Error = error;
        _all = result;
        IsLoading = false;
        ApplyFilter();
    }

    // A server-side filter change re-queries in the background; LoadAsync swallows its
    // own expected errors and raises Changed, so the view repaints when it lands. Only
    // cancellation is ignored here; an unexpected fault surfaces via the global handler
    // rather than vanishing into the discarded task (ADR 0013, L6).
    private void Reload() => _ = LoadAsync(_reloadToken).IgnoreCancellationAsync();

    private void ApplyFilter()
    {
        Rows = _filter.Length == 0
            ? _all
            : [.. _all.Where(Matches)];
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, Rows.Count - 1));
        Changed?.Invoke();
    }

    private bool Matches(WorkItem item) =>
        item.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.State.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.WorkItemType.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.Id.ToString().Contains(_filter, StringComparison.Ordinal);
}
