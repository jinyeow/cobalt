using System.Collections.ObjectModel;
using Cobalt.Core.Models;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>The three PR tabs with a bound ListView; Tab cycles tabs, Enter opens detail.</summary>
public sealed class PrListView : View
{
    private readonly IApplication _app;
    private readonly PrListViewModel _vm;
    private readonly PrCommentCountEnricher? _comments;
    private readonly Label _header;
    private readonly ListView _list;
    // View-lifetime token: cancelled once, on disposal.
    private readonly CancellationTokenSource _cts = new();
    // Per-load token (chained to the view token): cancelled and replaced on every
    // tab switch / scope change so a new load abandons the previous tab's in-flight
    // enrichment instead of leaving up to 200 stale fetches hogging the semaphore.
    private CancellationTokenSource _loadCts;
    private readonly Func<DateTimeOffset> _now;
    private IReadOnlyList<string> _rendered = [];
    private int _lastWidth = -1;
    private PrListFilter? _renderedTab;
    private bool _disposed;
    // MISSED-A: the (rows, width, counts) a render last actually formatted for. A render whose
    // inputs match these skips the O(rows) re-format + SetSource. _countsSeen is bumped by every
    // landed comment count so a count arrival always busts the guard and repaints its badge.
    private IReadOnlyList<PullRequest>? _formattedRows;
    private int _formattedWidth = -2;
    private int _formattedCountsSeen = -1;
    private int _countsSeen;

    public PrListView(IApplication app, PrListViewModel vm, PrCommentCountEnricher? comments = null, Func<DateTimeOffset>? now = null)
    {
        _app = app;
        _vm = vm;
        _comments = comments;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _now = now ?? (() => DateTimeOffset.Now);
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _list = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
        };
        // Disable type-ahead search so vim keys (j/k/g/G/…) bubble to the shell router.
        _list.KeystrokeNavigator = null;
        // Re-render when the terminal (and hence the list) is resized so columns
        // and the title reflow to the new width.
        _list.ViewportChanged += OnViewportChanged;
        Add(_header, _list);

        _vm.Changed += OnVmChanged;
        if (_comments is not null)
        {
            _comments.CountAvailable += OnCountAvailable;
        }
        Render();
    }

    public event Action<int>? ItemActivated;

    public void Load() => _ = Observe(_vm.LoadAsync(CycleLoadToken()));

    public void NextTab() => _ = Observe(_vm.NextTabAsync(CycleLoadToken()));

    /// <summary>Applies the client-side project narrowing to the already-loaded rows (null clears it).</summary>
    public void SetProjectFilter(string? project) => _vm.ProjectFilter = project ?? "";

    public void PrevTab() => _ = Observe(_vm.PrevTabAsync(CycleLoadToken()));

    /// <summary>
    /// Reloads the current tab and resets the selection to the top (row 0). Used on a
    /// <c>:scope</c> change, where the row set changes entirely (org ↔ project) so the
    /// old row index is meaningless — unlike a same-tab background refresh, which keeps it.
    /// </summary>
    public void ReloadFromTop()
    {
        _renderedTab = null; // make the next render treat this as a fresh set → reset to row 0
        Load();
    }

    /// <summary>Cancels the previous load's enrichment and starts a fresh per-load token chained to the view token.</summary>
    private CancellationToken CycleLoadToken()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        return _loadCts.Token;
    }

    /// <summary>Test seam: the current load generation's token (live until the next tab/scope switch).</summary>
    internal CancellationToken CurrentLoadToken => _loadCts.Token;

    public void OnOpen()
    {
        _vm.SelectedIndex = _list.SelectedItem ?? 0;
        if (_vm.Selected is { } pr)
        {
            ItemActivated?.Invoke(pr.PullRequestId);
        }
    }

    /// <summary>Vim movement (with optional count) forwarded from the shell router to the bound ListView.</summary>
    public void Navigate(AppCommand command, int? count = null)
    {
        VimScroll.Apply(_list, command, count);
        // The list is the source of truth for the cursor; mirror it back so a
        // background reload restores where the user actually is, not a stale index.
        _vm.SelectedIndex = _list.SelectedItem ?? 0;
        // Top up enrichment for the current viewport: a move within the visible window doesn't raise
        // ViewportChanged, so cover that edge here (the enricher dedupes already-fetched rows).
        EnqueueVisible();
    }

    public PullRequest? SelectedPr
    {
        get
        {
            _vm.SelectedIndex = _list.SelectedItem ?? 0;
            return _vm.Selected;
        }
    }

    private async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // section switched away mid-load
        }
    }

    private void OnVmChanged()
    {
        if (_disposed)
        {
            return;
        }
        _app.Invoke(() =>
        {
            if (!_disposed)
            {
                Render();
            }
        });
    }

    private void OnViewportChanged(object? sender, Terminal.Gui.ViewBase.DrawEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        if (_list.Viewport.Width != _lastWidth)
        {
            Render(); // width changed: reflow columns + widths (also enqueues the visible slice)
            return;
        }
        // A vertical scroll (ViewportChanged fires on Viewport.Y in 2.4.16, confirmed) brought new
        // rows into view — enrich just those, without re-formatting the unchanged row set (CACHE-2).
        EnqueueVisible();
    }

    // Coalesce a burst of per-PR comment-count arrivals into a single re-render. Each count
    // used to trigger a full re-format of every row (O(rows) per count → O(rows²) for a load of
    // 50–200 PRs). Now only the call that flips the flag schedules the invoke; counts landing
    // before it runs are all reflected by the queued Render (it reads every cached count), then
    // the flag resets so the next burst re-arms.
    private int _countRenderQueued;

    private void OnCountAvailable(int prId)
    {
        if (_disposed)
        {
            return;
        }
        // Bump before the coalescing guard so a count that lands while a render is already queued
        // still busts the render's MISSED-A skip check (its badge must repaint).
        Interlocked.Increment(ref _countsSeen);
        if (Interlocked.Exchange(ref _countRenderQueued, 1) == 1)
        {
            return;
        }
        _app.Invoke(() =>
        {
            Interlocked.Exchange(ref _countRenderQueued, 0);
            if (!_disposed)
            {
                Render();
            }
        });
    }

    /// <summary>
    /// The loaded rows currently on screen, padded by a small margin above and below so a short
    /// scroll finds counts already warm. Returns <see cref="PrListViewModel.Rows"/> whole when the
    /// whole list fits, so a small list keeps enriching every row (CACHE-2).
    /// </summary>
    private IReadOnlyList<PullRequest> VisibleSlice()
    {
        var rows = _vm.Rows;
        const int margin = 10;
        var height = Math.Max(1, _list.Viewport.Height);
        var top = Math.Max(0, _list.Viewport.Y - margin);
        var end = Math.Min(rows.Count, _list.Viewport.Y + height + margin);
        if (top == 0 && end == rows.Count)
        {
            return rows;
        }
        var slice = new List<PullRequest>(Math.Max(0, end - top));
        for (var i = top; i < end; i++)
        {
            slice.Add(rows[i]);
        }
        return slice;
    }

    /// <summary>Enqueues the on-screen slice for background comment-count enrichment (no-op with no rows).</summary>
    private void EnqueueVisible()
    {
        if (_comments is not null && _vm.Rows.Count > 0)
        {
            _comments.Enqueue(VisibleSlice(), _loadCts.Token);
        }
    }

    internal int ListWidth => _list.Viewport.Width;

    internal string RowText(int index) =>
        index >= 0 && index < _rendered.Count ? _rendered[index] : "";

    /// <summary>Test seam: the formatted-row list, whose reference changes only when a render
    /// actually re-formats the rows (MISSED-A). A skipped render leaves it untouched.</summary>
    internal IReadOnlyList<string> RenderedRows => _rendered;

    internal void Render()
    {
        var tab = _vm.ActiveTab switch
        {
            PrListFilter.ReviewQueue => "review queue",
            PrListFilter.Team => "team",
            PrListFilter.Mine => "mine",
            _ => "active",
        };
        _header.Text = _vm.IsLoading
            ? $" pull requests · {tab} · loading…"
            : _vm.Error is { } e
                ? $" pull requests · {tab} · error: {e}"
                : $" pull requests · {tab} ({_vm.Rows.Count})   [Tab] switch";

        var width = _list.Viewport.Width;
        _lastWidth = width;

        // On a tab/scope change the row set is different, so the previous tab's row index
        // must not carry over — reset the selection to the top. A same-tab background reload
        // keeps the reviewer's position (snap-back below).
        var tabChanged = _renderedTab != _vm.ActiveTab;
        _renderedTab = _vm.ActiveTab;
        if (tabChanged)
        {
            _vm.SelectedIndex = 0;
        }

        // MISSED-A: skip the O(rows) re-format + SetSource when nothing that feeds the rows
        // changed — same row set (by reference), same width, same landed counts. The header above
        // still refreshes loading/error/count, so a spurious Changed only repaints chrome.
        var countsSeen = Volatile.Read(ref _countsSeen);
        if (!tabChanged
            && ReferenceEquals(_vm.Rows, _formattedRows)
            && width == _formattedWidth
            && countsSeen == _formattedCountsSeen)
        {
            SetNeedsDraw();
            return;
        }

        var now = _now();
        var cols = PrColumns.For(_vm.Rows);

        // SetSource nulls SelectedItem in 2.4.16, so capture the reviewer's current
        // row first and restore it (clamped) — otherwise a background reload snaps
        // the highlight back to the top. The list is the source of truth.
        var target = tabChanged ? 0 : (_list.SelectedItem ?? _vm.SelectedIndex);
        _rendered = [.. _vm.Rows.Select(pr => PrRowFormatter.Format(pr, width, cols, now, _comments?.TryGet(pr)))];
        var rows = new ObservableCollection<string>(_rendered);
        _list.SetSource(rows);
        if (_vm.Rows.Count > 0)
        {
            _list.SelectedItem = Math.Clamp(target, 0, _vm.Rows.Count - 1);
        }

        // Lazily fill comment counts for the on-screen rows in the background; cached and capped,
        // so it never blocks this render and re-renders each row as counts land. Only the visible
        // slice (+margin) is enqueued (CACHE-2); scrolling tops up the rest via ViewportChanged.
        EnqueueVisible();

        _formattedRows = _vm.Rows;
        _formattedWidth = width;
        _formattedCountsSeen = countsSeen;
        SetNeedsDraw();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _vm.Changed -= OnVmChanged;
            _list.ViewportChanged -= OnViewportChanged;
            if (_comments is not null)
            {
                _comments.CountAvailable -= OnCountAvailable;
            }
            _cts.Cancel(); // also cancels the linked _loadCts
            _loadCts.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
