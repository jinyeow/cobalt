using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>Abstraction over WorkItemsApi so the view-model is testable without HTTP.</summary>
public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(CancellationToken ct);
}

/// <summary>State for the "my work items" list: async load, error surface, live filter, selection.</summary>
public sealed class WorkItemListViewModel(IWorkItemSource source)
{
    private IReadOnlyList<WorkItem> _all = [];
    private string _filter = "";
    private int _selectedIndex;

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

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = Math.Clamp(value, 0, Math.Max(0, Rows.Count - 1));
    }

    public WorkItem? Selected =>
        Rows.Count == 0 ? null : Rows[Math.Clamp(_selectedIndex, 0, Rows.Count - 1)];

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            _all = await source.QueryMyWorkItemsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
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
