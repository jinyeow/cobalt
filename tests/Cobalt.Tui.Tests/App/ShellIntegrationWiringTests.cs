using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Unit E integration seams provable without a running application: the <c>:log</c> observer
/// plumbing (<see cref="CobaltTuiApp.OperationObserver"/>), that the shell reaches both list-build
/// paths (which carry the empty-state scope accessor), and that the accessor the shell hands each
/// list VM (<c>() =&gt; _vm.Scope</c>) reflects live shell scope. A completed list load can't be
/// observed here: the view marshals its refresh through <c>app.Invoke</c>, which needs
/// <c>Init</c> (UAT-only), so the scope-hint string itself is unit-tested at the VM level.
/// </summary>
public class ShellIntegrationWiringTests
{
    private static readonly IApplication App = Application.Create();

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    /// <summary>Answers any ADO call with an empty collection so a list build settles to zero rows.</summary>
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[],"workItems":[]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class EmptyWorkItemSource : IWorkItemSource
    {
        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItem>>([]);
    }

    // One class-lifetime client: the stub handler is stateless, and a shared instance keeps
    // the adapters' plumbing out of each test's dispose bookkeeping.
    private static readonly HttpClient StubClient =
        new(new EmptyHandler()) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };

    private static WorkItemStoreAdapter WorkItemAdapter() =>
        new(new WorkItemsApi(new AdoHttp(StubClient), Context), PrScope.Project);

    private static PullRequestStoreAdapter PrAdapter() =>
        new(
            new GitApi(new AdoHttp(StubClient), Context),
            _ => Task.FromResult(Guid.Empty),
            _ => Task.FromResult(new TeamDirectory([])),
            project: Context.Project);

    [Fact]
    public void Operation_Observer_Marshals_Each_Record_Into_The_Log()
    {
        var log = new OperationLog();
        var op = AdoOperation.FromRoute(
            "GET", "_apis/connectionData?api-version=7.2", TimeSpan.FromMilliseconds(12), 200, DateTimeOffset.UtcNow);

        // A synchronous marshal stands in for app.Invoke so the record lands without a run loop.
        var observer = CobaltTuiApp.OperationObserver(log, run => run());
        observer(op);

        Assert.Same(op, Assert.Single(log.History));
    }

    [Fact]
    public void Shell_Reaches_Both_List_Build_Paths_That_Carry_The_Scope_Accessor()
    {
        var vm = new ShellViewModel(["work"], "work", PrScope.Project, ThemeChoice.Dark);
        using var shell = new CobaltShell(App, vm, workItems: WorkItemAdapter(), pullRequests: PrAdapter());

        // WorkItems is the default section (built at construction); switching builds the PR list.
        // Both BuildXxxList methods pass scope: () => _vm.Scope, so reaching them wires the accessor.
        Assert.NotNull(shell.WorkItemListVm);

        vm.HandleCommand(AppCommand.SectionPullRequests);
        Assert.NotNull(shell.PrListVm);
    }

    [Fact]
    public async Task Empty_State_Scope_Accessor_Reflects_Live_Shell_Scope()
    {
        // The exact accessor shape the shell hands the list VM (CobaltShell.BuildWorkItemList):
        // () => shellVm.Scope. Driving the VM directly avoids the view's app.Invoke refresh.
        var shellVm = new ShellViewModel(["work"], "work", PrScope.Org, ThemeChoice.Dark);
        var listVm = new WorkItemListViewModel(new EmptyWorkItemSource(), scope: () => shellVm.Scope);
        await listVm.LoadAsync(TestContext.Current.CancellationToken);

        // Org (the default): :scope org would be a no-op, so it is not offered.
        Assert.DoesNotContain(":scope org", listVm.EmptyStateText);

        // Flip the shell to project scope exactly as :scope project does; the live accessor picks it
        // up, so the empty-state hint now offers widening back to org.
        shellVm.HandlePaletteInput("scope project");
        Assert.Contains(":scope org", listVm.EmptyStateText);
    }

    private static KeyBindingTable RemapGlobal(string command, string sequence) =>
        KeyBindingTable.FromConfig(new KeysConfig(
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
            {
                ["global"] = new Dictionary<string, IReadOnlyList<string>> { [command] = new[] { sequence } },
            }));

    [Fact]
    public void Remapped_Movement_Key_Routes_Through_The_Injected_Table()
    {
        // move-down remapped j -> n. The shell's router is built from the injected table, so 'n'
        // must reach the movement-dispatch path (observed via MovementRedrawOverride, the seam a
        // real list Navigate fires). With the default Shared table 'n' is unbound and nothing fires.
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm, bindings: RemapGlobal("move-down", "n"));
        bool? forced = null;
        shell.MovementRedrawOverride = f => forced = f;
        shell.SetFocus();

        shell.NewKeyDownEvent(new Key('n'));

        Assert.Equal(false, forced); // a movement was dispatched (non-forced redraw) via the remapped key
    }

    [Fact]
    public void Log_Palette_Command_Reaches_The_Subscribed_Shell()
    {
        var vm = new ShellViewModel(["work"], "work");
        using var shell = new CobaltShell(App, vm);
        OperationLog? shown = null;
        shell.ShowLogOverride = ops => shown = ops;

        vm.HandlePaletteInput("log"); // parser -> LogRequested -> shell's ShowLog

        Assert.Same(vm.Operations, shown);
    }
}
