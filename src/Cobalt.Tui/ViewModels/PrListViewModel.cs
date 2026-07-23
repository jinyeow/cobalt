using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

public interface IPullRequestSource
{
    Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct);
}

/// <summary>
/// The PR tabs (review queue / team / mine / active) with async load, error, repo filter.
/// <paramref name="scope"/> reads the shell's current PR/WI scope so
/// <see cref="EmptyStateText"/> only suggests <c>:scope org</c> when it would actually change
/// anything (Project scope) — org is the product default, so a caller that omits this behaves
/// as if scope were always Org. Unit E: pass <c>() =&gt; _vm.Scope</c> (<c>ShellViewModel.Scope</c>)
/// when constructing this view-model.
/// </summary>
public sealed class PrListViewModel(IPullRequestSource source, Func<PrScope>? scope = null)
{
    /// <summary>
    /// The canonical sub-tab cycle order ([ / ] / Tab walk it). The tab strip renders
    /// from this same array so the visible order can never diverge from the cycle.
    /// The personal ReviewQueue is deliberately not in the cycle: orgs that request
    /// reviews via teams (the common setup) always see it empty — the Team tab is
    /// the real queue there, so it leads as the default. The filter itself remains
    /// supported (adapter + API) for a future config-enabled view.
    /// </summary>
    internal static readonly PrListFilter[] TabOrder =
        [PrListFilter.Team, PrListFilter.Mine, PrListFilter.Active];

    private IReadOnlyList<PullRequest> _all = [];
    private string _repositoryFilter = "";
    private string _projectFilter = "";
    private readonly Func<PrScope> _scope = scope ?? (() => PrScope.Org);
    private int _selectedIndex;
    private int _loadSeq;

    // CACHE-3: the last successful raw (unfiltered) result per tab, so revisiting a tab paints
    // instantly then refreshes. Holds server results; the client-side repo/project filters are
    // reapplied on paint, so a filter change never needs to invalidate this. Cleared on a
    // scope/context change (InvalidateCache), which alters the underlying query.
    // Guarded by _cacheLock together with _loadSeq: the commit runs on a threadpool continuation
    // while the UI thread reads/clears, so the cache write, the seq supersede-check, and the clear
    // must be atomic w.r.t. one another (a :scope flip clearing then a stale write racing in).
    private readonly Dictionary<PrListFilter, IReadOnlyList<PullRequest>> _tabCache = [];
    private readonly object _cacheLock = new();

    public PrListFilter ActiveTab { get; private set; } = PrListFilter.Team;
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<PullRequest> Rows { get; private set; } = [];

    /// <summary>
    /// Raised when the row set / loading state changes. May fire on a threadpool continuation (an ADO
    /// list load completing), so a subscriber that touches Terminal.Gui must marshal onto the UI
    /// thread via <see cref="App.IUiPost"/> — never <c>IApplication</c>, which this UI-free
    /// view-model (ADR 0004) deliberately does not reference.
    /// </summary>
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

    /// <summary>Client-side narrowing by <see cref="PullRequest.ProjectName"/>; empty clears it.</summary>
    public string ProjectFilter
    {
        get => _projectFilter;
        set
        {
            _projectFilter = value ?? "";
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

    /// <summary>
    /// Guidance for an empty list — non-null only once loading/error are ruled out, so it never
    /// flickers over a transient state. A client-side filter narrowed to zero names the filter and
    /// how to clear it (only when the server actually returned something to narrow — otherwise the
    /// filter isn't the cause); a genuinely empty Team tab (the org-dependent default — see the
    /// class doc comment) explains that it's empty by design rather than broken; any other
    /// genuinely empty tab gets a plain fallback. RepositoryFilter has no palette command wired to
    /// it yet, so it deliberately isn't named here (would send the reviewer to a command that
    /// doesn't exist).
    /// </summary>
    public string? EmptyStateText
    {
        get
        {
            if (IsLoading || Error is not null || Rows.Count != 0)
            {
                return null;
            }

            if (_projectFilter.Length != 0 && _all.Count != 0)
            {
                return $"0 of {_all.Count} PRs in project \"{_projectFilter}\" — clear with :project (no argument).";
            }

            if (ActiveTab != PrListFilter.Team)
            {
                return "Nothing here.";
            }

            // :scope org is only offered when it would actually change something.
            var scopeHint = _scope() == PrScope.Project ? " Try :scope org, or ] for mine/active." : " Try ] for mine/active.";
            return "No PRs waiting on your teams — empty, not broken: team-based review-request setup varies by org." + scopeHint;
        }
    }

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
        // Stamp this load; only the newest may commit its results (kills the race where a slow
        // first fetch lands after a newer tab's fetch — B3/D2). Stamp and read the cache under the
        // lock so both are consistent with a concurrent invalidation.
        int seq;
        IReadOnlyList<PullRequest> cached;
        lock (_cacheLock)
        {
            seq = ++_loadSeq;
            cached = _tabCache.GetValueOrDefault(tab, []);
        }

        ActiveTab = tab;
        IsLoading = true;
        Error = null;
        // CACHE-3: paint the tab's last-known rows immediately, then refresh under the _loadSeq
        // guard below. A tab not visited this session (or after InvalidateCache) has no cache, so
        // the pane still blanks the instant Tab is pressed instead of showing the previous tab's
        // PRs during the round-trip (the original D1 behaviour, now only for a cold tab).
        _all = cached;
        ApplyFilter();

        string? error = null;
        var result = await VmGuard.RunAsync(
            () => source.ListPullRequestsAsync(tab, ct), ct, m => error = m).ConfigureAwait(false);

        // Commit under the lock so the supersede-check and cache write are atomic w.r.t. a newer
        // load or an InvalidateCache (a :scope flip): a superseded or invalidated load must neither
        // paint nor write a stale result.
        lock (_cacheLock)
        {
            if (seq != _loadSeq)
            {
                return; // superseded by a newer load or an invalidation — drop this result
            }
            if (result is not null)
            {
                // Only a successful fetch updates the cache; on a transient error the painted rows
                // (cached, or the cold-start empty) stay put so the tab isn't blanked under an error.
                _tabCache[tab] = result;
            }
        }

        Error = error;
        if (result is not null)
        {
            _all = result;
        }
        IsLoading = false;
        ApplyFilter();
    }

    /// <summary>
    /// Drops every tab's cached rows so the next visit refetches from the server rather than
    /// painting stale rows, and supersedes any in-flight load so its result cannot land after the
    /// clear. Called when the underlying query changes (a <c>:scope</c> or context switch); the
    /// client-side repo/project filters do not need it (they narrow the raw cache).
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _tabCache.Clear();
            _loadSeq++; // supersede any in-flight load so a stale write cannot land after the clear
        }
    }

    /// <summary>
    /// Drops only the active tab's cached rows so a mutation refresh (vote/abandon/complete) shows
    /// fresh data — or, on a transient refresh failure, a blank pane rather than a stale row that no
    /// longer reflects the change just made.
    /// </summary>
    public void InvalidateActiveTab()
    {
        lock (_cacheLock)
        {
            _tabCache.Remove(ActiveTab);
        }
    }

    private void ApplyFilter()
    {
        Rows =
        [
            .. _all.Where(pr =>
                (_repositoryFilter.Length == 0 ||
                 pr.RepositoryName.Contains(_repositoryFilter, StringComparison.OrdinalIgnoreCase)) &&
                // Exact project match (not substring): `:project Web` must not keep "WebApps",
                // mirroring the WI side's WIQL equality (M4). Repo filter stays substring.
                (_projectFilter.Length == 0 ||
                 string.Equals(pr.ProjectName, _projectFilter, StringComparison.OrdinalIgnoreCase))),
        ];
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, Rows.Count - 1));
        Changed?.Invoke();
    }
}
