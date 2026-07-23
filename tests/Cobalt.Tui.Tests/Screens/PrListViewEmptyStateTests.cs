using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>View-level, headless: the empty-list placeholder shows the VM's <see cref="PrListViewModel.EmptyStateText"/>.</summary>
public class PrListViewEmptyStateTests
{
    // Undrained by design: mirrors today's headless "Invoke never drains" so the views' posted
    // renders never fire on their own — tests call view.Render() explicitly (M2).
    private static readonly RecordingUiPost App = new();

    private sealed class FakePrSource(IReadOnlyList<PullRequest> items) : IPullRequestSource
    {
        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task Placeholder_Row_Shows_The_Vm_EmptyStateText()
    {
        var vm = new PrListViewModel(new FakePrSource([])); // Team tab, empty — org-dependent guidance
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var view = new PrListView(App, vm);
        using var window = new Window();
        window.Add(view);
        window.Layout(new System.Drawing.Size(80, 20));

        Assert.Equal(vm.EmptyStateText, view.RowText(0));
    }
}
