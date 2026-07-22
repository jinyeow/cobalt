using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>View-level, headless: the empty-list placeholder shows the VM's <see cref="PrListViewModel.EmptyStateText"/>.</summary>
public class PrListViewEmptyStateTests
{
    private static readonly IApplication App = Application.Create();

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
