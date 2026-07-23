using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class WorkItemListViewModelTests
{
    private static WorkItem Item(long id, string title, string state, string type = "Bug") =>
        new(id, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = JsonEl($"\"{title}\""),
            ["System.State"] = JsonEl($"\"{state}\""),
            ["System.WorkItemType"] = JsonEl($"\"{type}\""),
        });

    private static JsonElement JsonEl(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeSource(IReadOnlyList<WorkItem> items) : IWorkItemSource
    {
        public Exception? Throw { get; init; }
        public int Calls { get; private set; }
        public WorkItemQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct)
        {
            Calls++;
            LastQuery = query;
            return Throw is not null ? Task.FromException<IReadOnlyList<WorkItem>>(Throw) : Task.FromResult(items);
        }
    }

    [Fact]
    public async Task Load_Populates_Rows_And_Clears_Loading()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active"), Item(2, "B", "New")]));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("A", vm.Rows[0].Title);
    }

    [Fact]
    public async Task Load_Expected_Failure_Sets_Error_And_Clears_Rows()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active")])
        {
            Throw = new HttpRequestException("boom"),
        });

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.Error);
        Assert.Contains("boom", vm.Error);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Load_User_Cancellation_Propagates()
    {
        // A genuine user/dialog cancel carries the caller's own token — it must propagate.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new OperationCanceledException(cts.Token) });

        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Load_Timeout_Cancellation_Surfaces_As_Error()
    {
        // An HttpClient timeout surfaces as an OCE whose token is NOT the caller's; it must
        // become a visible error and reset IsLoading, not a permanent "loading…" pane (LOW-1).
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new OperationCanceledException(foreign.Token) });

        await vm.LoadAsync(TestContext.Current.CancellationToken); // caller token is NOT cancelled

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Load_Unexpected_Exception_Propagates()
    {
        // An unexpected type is a bug, not an API error — it must escape to the global
        // crash boundary rather than being swallowed into the Error surface.
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new InvalidOperationException("bug") });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Filter_Narrows_Visible_Rows_By_Title_And_Id()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "Fix login", "Active"), Item(2, "Add logs", "New")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "login";
        Assert.Single(vm.Rows);
        Assert.Equal("Fix login", vm.Rows[0].Title);

        vm.Filter = "2";
        Assert.Single(vm.Rows);
        Assert.Equal(2, vm.Rows[0].Id);

        vm.Filter = "";
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Filter_Is_Case_Insensitive()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "Fix Login", "Active")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "LOGIN";

        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Selected_Item_Tracks_Index()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "A", "Active"), Item(2, "B", "New")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.SelectedIndex = 1;

        Assert.Equal(2, vm.Selected?.Id);
    }

    [Fact]
    public async Task Selected_Is_Null_When_Empty()
    {
        var vm = new WorkItemListViewModel(new FakeSource([]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(vm.Selected);
    }

    [Fact]
    public async Task Default_Query_Hides_Completed_And_Has_No_Project_Filter()
    {
        var source = new FakeSource([Item(1, "A", "Active")]);
        var vm = new WorkItemListViewModel(source);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IncludeCompleted);
        Assert.Null(vm.ProjectFilter);
        Assert.False(source.LastQuery!.IncludeCompleted);
        Assert.Null(source.LastQuery.Project);
    }

    [Fact]
    public async Task Toggling_IncludeCompleted_Requeries_With_New_Option()
    {
        var source = new FakeSource([Item(1, "A", "Active")]);
        var vm = new WorkItemListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var before = source.Calls;

        vm.IncludeCompleted = true; // setter triggers a reload (fake completes synchronously)

        Assert.Equal(before + 1, source.Calls);
        Assert.True(source.LastQuery!.IncludeCompleted);
    }

    [Fact]
    public async Task Setting_ProjectFilter_Requeries_With_Project_Clause()
    {
        var source = new FakeSource([Item(1, "A", "Active")]);
        var vm = new WorkItemListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.ProjectFilter = "Fabrikam";

        Assert.Equal("Fabrikam", vm.ProjectFilter);
        Assert.Equal("Fabrikam", source.LastQuery!.Project);
    }

    [Fact]
    public async Task Clearing_ProjectFilter_Requeries_Without_Project_Clause()
    {
        var source = new FakeSource([Item(1, "A", "Active")]);
        var vm = new WorkItemListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.ProjectFilter = "Fabrikam";

        vm.ProjectFilter = null;

        Assert.Null(vm.ProjectFilter);
        Assert.Null(source.LastQuery!.Project);
    }

    [Fact]
    public async Task Setting_Same_Filter_Value_Does_Not_Requery()
    {
        var source = new FakeSource([Item(1, "A", "Active")]);
        var vm = new WorkItemListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var before = source.Calls;

        vm.IncludeCompleted = false; // already false — no-op

        Assert.Equal(before, source.Calls);
    }

    [Fact]
    public async Task EmptyStateText_Is_Null_While_Loading()
    {
        var source = new GatedSource();
        var vm = new WorkItemListViewModel(source);
        var gate = source.NextGate();
        var loading = vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.IsLoading);
        Assert.Null(vm.EmptyStateText);

        gate.SetResult([]);
        await loading;
    }

    [Fact]
    public async Task EmptyStateText_Is_Null_On_Error()
    {
        var vm = new WorkItemListViewModel(new FakeSource([]) { Throw = new HttpRequestException("boom") });

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Null(vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_On_Genuinely_Empty_Suggests_Done_And_Scope_When_Project_Scoped()
    {
        var vm = new WorkItemListViewModel(new FakeSource([]), scope: () => PrScope.Project);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Contains(":done show", vm.EmptyStateText);
        Assert.Contains(":scope org", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Omits_Scope_Hint_When_Already_Org_Scoped()
    {
        // :scope org is a no-op when org is already the active scope (the default) — suggesting
        // it would send the reviewer to run a command that changes nothing.
        var vm = new WorkItemListViewModel(new FakeSource([]), scope: () => PrScope.Org);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(":scope org", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Omits_Scope_Hint_By_Default_With_No_Scope_Accessor()
    {
        // Org is the product default (ADR/PrScope doc), so omitting the ctor param must behave
        // the same as passing Org — never assume Project.
        var vm = new WorkItemListViewModel(new FakeSource([]));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(":scope org", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Omits_Done_Hint_When_Completed_Already_Shown()
    {
        // IncludeCompleted = true is already the widest :done setting — suggesting :done show
        // again would be a no-op instruction.
        var source = new FakeSource([]);
        var vm = new WorkItemListViewModel(source, includeCompleted: true);

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(":done show", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Names_Substring_Filter_When_Filtered_To_Zero()
    {
        var vm = new WorkItemListViewModel(new FakeSource([Item(1, "Fix login", "Active")]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "no-such-title";

        Assert.Empty(vm.Rows);
        Assert.Contains("no-such-title", vm.EmptyStateText);
        // Esc only hides the filter field — it does not clear vm.Filter — so the hint must not
        // claim it does; it must point at the action that actually clears the search.
        Assert.DoesNotContain("Esc", vm.EmptyStateText);
        Assert.Contains("reopen /", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Falls_Through_To_Genuine_Empty_When_Filter_Matches_Nothing_At_All()
    {
        // The server already returned zero rows — the substring filter did not cause the
        // emptiness, so "0 of 0 match ... clear to see them all" would be a false claim.
        var vm = new WorkItemListViewModel(new FakeSource([]));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "anything";

        Assert.Empty(vm.Rows);
        Assert.DoesNotContain("0 of 0", vm.EmptyStateText);
        Assert.Contains("assigned to you", vm.EmptyStateText);
    }

    [Fact]
    public async Task EmptyStateText_Names_Project_Filter_When_Filtered_To_Zero()
    {
        // Seed the project filter via the ctor and await LoadAsync directly, rather than setting
        // vm.ProjectFilter (which reloads via a fire-and-forget task with no awaitable handle) —
        // asserting right after that setter is only deterministic because FakeSource resolves
        // synchronously; this proves the same EmptyStateText behaviour without relying on that.
        var vm = new WorkItemListViewModel(new FakeSource([]), projectFilter: "Contoso");

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.Rows);
        Assert.Contains("Contoso", vm.EmptyStateText);
        Assert.Contains(":project", vm.EmptyStateText);
    }

    private sealed class GatedSource : IWorkItemSource
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<WorkItem>>> _gates = new();

        public TaskCompletionSource<IReadOnlyList<WorkItem>> NextGate()
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<WorkItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(tcs);
            return tcs;
        }

        public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
            _gates.Dequeue().Task;
    }

    [Fact]
    public async Task Slow_First_Load_Cannot_Clobber_A_Newer_Faster_Load()
    {
        // Load A (slow) starts, then load B (fast) supersedes it. B completes first and
        // commits; when A finally lands it must be dropped, leaving B's rows (M1).
        var source = new GatedSource();
        var vm = new WorkItemListViewModel(source);

        var gateA = source.NextGate();
        var loadA = vm.LoadAsync(TestContext.Current.CancellationToken);

        var gateB = source.NextGate();
        var loadB = vm.LoadAsync(TestContext.Current.CancellationToken);

        gateB.SetResult([Item(2, "B", "New")]);
        await loadB;
        Assert.Equal("B", vm.Rows.Single().Title);

        gateA.SetResult([Item(1, "A", "Active")]);
        await loadA;

        Assert.Equal("B", vm.Rows.Single().Title); // the slow load did not overwrite the fast one
    }

    [Fact]
    public async Task Substring_Filter_Composes_On_Top_Of_Server_Query()
    {
        // The `/` client filter narrows within whatever the server returned.
        var source = new FakeSource([Item(1, "Fix login", "Active"), Item(2, "Add logs", "New")]);
        var vm = new WorkItemListViewModel(source);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.Filter = "login";
        Assert.Single(vm.Rows);

        vm.IncludeCompleted = true; // server re-query keeps the same 2 rows...
        Assert.Single(vm.Rows);     // ...but the `/` filter still applies
        Assert.Equal("Fix login", vm.Rows[0].Title);
    }
}
