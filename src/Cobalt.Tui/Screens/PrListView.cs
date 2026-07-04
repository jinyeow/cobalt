using System.Collections.ObjectModel;
using Cobalt.Core.Models;
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
    private readonly Label _header;
    private readonly ListView _list;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public PrListView(IApplication app, PrListViewModel vm)
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
            Height = Dim.Fill(),
            CanFocus = true,
        };
        Add(_header, _list);

        _vm.Changed += OnVmChanged;
        Render();
    }

    public event Action<int>? ItemActivated;

    public void Load() => _ = Observe(_vm.LoadAsync(_cts.Token));

    public void NextTab() => _ = Observe(_vm.NextTabAsync(_cts.Token));

    public void PrevTab() => _ = Observe(_vm.PrevTabAsync(_cts.Token));

    public void OnOpen()
    {
        _vm.SelectedIndex = _list.SelectedItem ?? 0;
        if (_vm.Selected is { } pr)
        {
            ItemActivated?.Invoke(pr.PullRequestId);
        }
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

    private void Render()
    {
        var tab = _vm.ActiveTab switch
        {
            PrListFilter.ReviewQueue => "review queue",
            PrListFilter.Mine => "mine",
            _ => "active",
        };
        _header.Text = _vm.IsLoading
            ? $" pull requests · {tab} · loading…"
            : _vm.Error is { } e
                ? $" pull requests · {tab} · error: {e}"
                : $" pull requests · {tab} ({_vm.Rows.Count})   [Tab] switch";

        var rows = new ObservableCollection<string>(_vm.Rows.Select(Format));
        _list.SetSource(rows);
        if (_vm.Rows.Count > 0)
        {
            _list.SelectedItem = Math.Clamp(_vm.SelectedIndex, 0, _vm.Rows.Count - 1);
        }
        SetNeedsDraw();
    }

    private static string Format(PullRequest pr)
    {
        var id = $"!{pr.PullRequestId}".PadRight(7);
        var repo = Truncate(pr.RepositoryName, 12).PadRight(12);
        var votes = VoteSummary(pr);
        var draft = pr.IsDraft ? "[draft] " : "";
        return $"{id} {repo} {votes,-6} {draft}{Truncate(pr.Title, 50)}";
    }

    private static string VoteSummary(PullRequest pr)
    {
        if (pr.Reviewers.Any(r => r.Vote == PrVote.Rejected))
        {
            return "✗ rej";
        }
        if (pr.Reviewers.Any(r => r.Vote == PrVote.WaitingForAuthor))
        {
            return "⧗ wait";
        }
        var approved = pr.Reviewers.Count(r => r.Vote is PrVote.Approved or PrVote.ApprovedWithSuggestions);
        return approved > 0 ? $"✓ {approved}" : "·";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

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
}
