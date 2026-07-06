using Cobalt.Core.Ado;
using Cobalt.Core.Models;

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

    public async Task LoadAsync(CancellationToken ct)
    {
        // Remember the token so a filter setter can reload on its own later.
        _reloadToken = ct;
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            var query = new WorkItemQuery(_includeCompleted, _projectFilter);
            _all = await source.QueryMyWorkItemsAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (AdoExceptions.IsExpected(ex))
        {
            Error = ex.Message;
            _all = [];
        }
        finally
        {
            IsLoading = false;
            ApplyFilter();
        }
    }

    // A server-side filter change re-queries in the background; LoadAsync swallows its
    // own expected errors and raises Changed, so the view repaints when it lands.
    private void Reload() => _ = LoadAsync(_reloadToken);

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
