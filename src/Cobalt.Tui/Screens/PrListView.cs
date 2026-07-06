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
        if (_disposed || _list.Viewport.Width == _lastWidth)
        {
            return;
        }
        Render();
    }

    private void OnCountAvailable(int prId)
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

    internal int ListWidth => _list.Viewport.Width;

    internal string RowText(int index) =>
        index >= 0 && index < _rendered.Count ? _rendered[index] : "";

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
        var now = _now();
        var cols = PrColumns.For(_vm.Rows);

        // On a tab/scope change the row set is different, so the previous tab's row index
        // must not carry over — reset the selection to the top. A same-tab background reload
        // keeps the reviewer's position (snap-back below).
        var tabChanged = _renderedTab != _vm.ActiveTab;
        _renderedTab = _vm.ActiveTab;
        if (tabChanged)
        {
            _vm.SelectedIndex = 0;
        }

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

        // Lazily fill comment counts for the loaded rows in the background; cached and
        // capped, so it never blocks this render and re-renders each row as counts land.
        if (_comments is not null && _vm.Rows.Count > 0)
        {
            _comments.Enqueue(_vm.Rows, _loadCts.Token);
        }
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
