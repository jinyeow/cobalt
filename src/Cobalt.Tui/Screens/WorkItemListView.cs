using System.Collections.ObjectModel;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>The "my work items" list: a header, a bound ListView, and a filter line.</summary>
public sealed class WorkItemListView : View
{
    private readonly IApplication _app;
    private readonly WorkItemListViewModel _vm;
    private readonly Label _header;
    private readonly ListView _list;
    private readonly Label _filterLabel;
    private readonly TextField _filter;
    private readonly CancellationTokenSource _cts = new();
    private int _lastWidth = -1;
    private bool _filtering;
    private bool _disposed;

    public WorkItemListView(IApplication app, WorkItemListViewModel vm)
    {
        _app = app;
        _vm = vm;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _list = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            CanFocus = true,
        };
        // Disable type-ahead search: it consumes j/k/g/G and the shell's vim keys
        // before they can bubble up to the router. Navigation goes through the shell.
        _list.KeystrokeNavigator = null;
        // Re-render on resize so the width-aware columns reflow to the new width.
        _list.ViewportChanged += OnViewportChanged;
        _filterLabel = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = 1, Text = "/", Visible = false };
        _filter = new TextField { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Visible = false };

        Add(_header, _list, _filterLabel, _filter);

        _filter.TextChanged += (_, _) => _vm.Filter = _filter.Text?.ToString() ?? "";
        _filter.KeyDown += (_, key) =>
        {
            if (key.KeyCode is Terminal.Gui.Drivers.KeyCode.Enter or Terminal.Gui.Drivers.KeyCode.Esc)
            {
                StopFiltering();
                key.Handled = true;
            }
        };
        _vm.Changed += OnVmChanged;
        Render();
    }

    /// <summary>Fired when the user opens an item (Enter/o) — the shell shows detail.</summary>
    public event Action<long>? ItemActivated;

    /// <summary>Loads (or reloads) the list; safe to call repeatedly. Cancels on disposal.</summary>
    public void Load() => _ = LoadAndObserveAsync();

    private async Task LoadAndObserveAsync()
    {
        try
        {
            await _vm.LoadAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected when the section is switched away mid-load
        }
    }

    public void OnRefresh() => Load();

    private void OnVmChanged()
    {
        if (_disposed)
        {
            return; // a late background completion after a section switch
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _vm.Changed -= OnVmChanged;
            _list.ViewportChanged -= OnViewportChanged;
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    public void OnOpen()
    {
        _vm.SelectedIndex = _list.SelectedItem ?? 0;
        if (_vm.Selected is { } item)
        {
            ItemActivated?.Invoke(item.Id);
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

    public long? SelectedId
    {
        get
        {
            _vm.SelectedIndex = _list.SelectedItem ?? 0;
            return _vm.Selected?.Id;
        }
    }

    public void StartFiltering()
    {
        _filtering = true;
        _filterLabel.Visible = true;
        _filter.Visible = true;
        _filter.SetFocus();
    }

    private void StopFiltering()
    {
        _filtering = false;
        _filterLabel.Visible = false;
        _filter.Visible = false;
        _list.SetFocus();
    }

    public bool IsFiltering => _filtering;

    internal void Render()
    {
        if (_vm.IsLoading)
        {
            _header.Text = " loading work items…";
        }
        else if (_vm.Error is { } error)
        {
            _header.Text = $" error: {error}";
        }
        else
        {
            _header.Text = $" my work items ({_vm.Rows.Count})";
        }

        var width = _list.Viewport.Width;
        _lastWidth = width;

        // SetSource nulls SelectedItem in 2.4.16, so capture the reviewer's current
        // row first and restore it (clamped) — otherwise a background reload snaps
        // the highlight back to the top. The list is the source of truth.
        var target = _list.SelectedItem ?? _vm.SelectedIndex;
        var rows = new ObservableCollection<string>(_vm.Rows.Select(item => WorkItemRowFormatter.Format(item, width)));
        _list.SetSource(rows);
        if (_vm.Rows.Count > 0)
        {
            _list.SelectedItem = Math.Clamp(target, 0, _vm.Rows.Count - 1);
        }
        SetNeedsDraw();
    }
}
