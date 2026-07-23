using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.ViewModels;

/// <summary>Abstraction over WorkItemsApi so the view-model is testable without HTTP.</summary>
public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct);
}

/// <summary>
/// State for the "my work items" list: async load, error surface, live filter, selection.
/// <paramref name="scope"/> reads the shell's current PR/WI scope so
/// <see cref="EmptyStateText"/> only suggests <c>:scope org</c> when it would actually change
/// anything (Project scope) — org is the product default, so a caller that omits this behaves
/// as if scope were always Org. Unit E: pass <c>() =&gt; _vm.Scope</c> (<c>ShellViewModel.Scope</c>)
/// when constructing this view-model.
/// </summary>
public sealed class WorkItemListViewModel(
    IWorkItemSource source,
    bool includeCompleted = false,
    string? projectFilter = null,
    Func<PrScope>? scope = null)
{
    private IReadOnlyList<WorkItem> _all = [];
    private string _filter = "";
    private int _selectedIndex;
    private bool _includeCompleted = includeCompleted;
    private string? _projectFilter = string.IsNullOrEmpty(projectFilter) ? null : projectFilter;
    private readonly Func<PrScope> _scope = scope ?? (() => PrScope.Org);
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

            // Only blame the filter when there was something for it to narrow — if the server
            // itself returned nothing, the filter isn't the cause and "0 of 0" would be false.
            if (_filter.Length != 0 && _all.Count != 0)
            {
                // Esc only hides the filter field — it does not clear _filter — so the hint must
                // name the action that actually does: reopen `/` and clear the text.
                return $"0 of {_all.Count} work items match \"{_filter}\" — reopen / and clear it to see them all.";
            }

            if (_projectFilter is not null)
            {
                return $"No work items in project \"{_projectFilter}\" — clear with :project (no argument).";
            }

            // :done show and :scope org are only offered when they would actually change
            // something — suggesting a no-op command is worse than no hint at all.
            var hints = new List<string>();
            if (!_includeCompleted)
            {
                hints.Add(":done show includes completed states");
            }
            if (_scope() == PrScope.Project)
            {
                hints.Add(":scope org widens the query");
            }
            return hints.Count == 0
                ? "No work items assigned to you."
                : $"No work items assigned to you — {string.Join("; ", hints)}.";
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

        string? error = null;
        var query = new WorkItemQuery(_includeCompleted, _projectFilter);
        var result = await VmGuard.RunAsync(
            () => source.QueryMyWorkItemsAsync(query, ct), ct, m => error = m).ConfigureAwait(false) ?? [];

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
