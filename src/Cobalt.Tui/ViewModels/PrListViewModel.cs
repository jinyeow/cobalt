using Cobalt.Core.Ado;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

public interface IPullRequestSource
{
    Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct);
}

/// <summary>The three PR tabs (review queue / mine / active) with async load, error, repo filter.</summary>
public sealed class PrListViewModel(IPullRequestSource source)
{
    private static readonly PrListFilter[] TabOrder =
        [PrListFilter.ReviewQueue, PrListFilter.Mine, PrListFilter.Active];

    private IReadOnlyList<PullRequest> _all = [];
    private string _repositoryFilter = "";
    private int _selectedIndex;
    private int _loadSeq;

    public PrListFilter ActiveTab { get; private set; } = PrListFilter.ReviewQueue;
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<PullRequest> Rows { get; private set; } = [];

    public event Action? Changed;

    public string RepositoryFilter
    {
        get => _repositoryFilter;
        set
        {
            _repositoryFilter = value;
            ApplyFilter();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => _selectedIndex = Math.Clamp(value, 0, Math.Max(0, Rows.Count - 1));
    }

    public PullRequest? Selected =>
        Rows.Count == 0 ? null : Rows[Math.Clamp(_selectedIndex, 0, Rows.Count - 1)];

    public Task LoadAsync(CancellationToken ct) => LoadTabAsync(ActiveTab, ct);

    public Task SetTabAsync(PrListFilter tab, CancellationToken ct) => LoadTabAsync(tab, ct);

    public Task NextTabAsync(CancellationToken ct)
    {
        var next = TabOrder[(Array.IndexOf(TabOrder, ActiveTab) + 1) % TabOrder.Length];
        return LoadTabAsync(next, ct);
    }

    public Task PrevTabAsync(CancellationToken ct)
    {
        var prev = TabOrder[(Array.IndexOf(TabOrder, ActiveTab) - 1 + TabOrder.Length) % TabOrder.Length];
        return LoadTabAsync(prev, ct);
    }

    private async Task LoadTabAsync(PrListFilter tab, CancellationToken ct)
    {
        // Stamp this load; only the newest may commit its results (kills the race
        // where a slow first fetch lands after a newer tab's fetch — B3/D2).
        var seq = ++_loadSeq;

        ActiveTab = tab;
        IsLoading = true;
        Error = null;
        // Blank the pane the instant Tab is pressed instead of showing the previous
        // tab's PRs during the network round-trip (D1).
        Rows = [];
        Changed?.Invoke();

        IReadOnlyList<PullRequest> result;
        string? error = null;
        try
        {
            result = await source.ListPullRequestsAsync(tab, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
            result = [];
        }

        // A newer load superseded this one while it was in flight; drop its results
        // so it cannot clobber the current tab.
        if (seq != _loadSeq)
        {
            return;
        }

        Error = error;
        _all = result;
        IsLoading = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows = _repositoryFilter.Length == 0
            ? _all
            : [.. _all.Where(pr => pr.RepositoryName.Contains(_repositoryFilter, StringComparison.OrdinalIgnoreCase))];
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, Rows.Count - 1));
        Changed?.Invoke();
    }
}
