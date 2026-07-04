using System.Collections.ObjectModel;
using Cobalt.Core.Models;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _vm.Changed -= OnVmChanged;
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

    private void Render()
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

        var rows = new ObservableCollection<string>(_vm.Rows.Select(Format));
        _list.SetSource(rows);
        if (_vm.Rows.Count > 0)
        {
            _list.SelectedItem = Math.Clamp(_vm.SelectedIndex, 0, _vm.Rows.Count - 1);
        }
        SetNeedsDraw();
    }

    private static string Format(WorkItem item)
    {
        var id = item.Id.ToString().PadLeft(6);
        var type = Truncate(item.WorkItemType, 8).PadRight(8);
        var state = Truncate(item.State, 10).PadRight(10);
        var title = Truncate(item.Title, 60);
        return $"{id}  {type}  {state}  {title}";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
