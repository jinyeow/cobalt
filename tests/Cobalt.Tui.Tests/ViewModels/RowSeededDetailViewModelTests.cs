using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The preview's tier 1 (#49, ADR 0024): a detail view-model seeded with the list row renders
/// through the shared formatter with no store call at all. The work-item list route omits
/// <c>System.Description</c>, so the seeded view-model must cope with a row that has none —
/// this is the path a fresh task or bug takes on the work-items section at startup.
/// </summary>
public class RowSeededDetailViewModelTests
{
    /// <summary>Fails the test if tier 1 touches the network at all.</summary>
    private sealed class ThrowingWorkItemStore : IWorkItemStore
    {
        private static InvalidOperationException Fetched() => new("tier 1 must not fetch");

        public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) => throw Fetched();
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) => throw Fetched();
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct) => throw Fetched();
        public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) => throw Fetched();
        public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) => throw Fetched();
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A row exactly as the list route returns it: the ListFields only — no description,
    /// no priority, no story points.</summary>
    private static WorkItem ListRow() => new(7, new Dictionary<string, JsonElement>
    {
        ["System.Title"] = El("\"Token cache misses under load\""),
        ["System.State"] = El("\"Active\""),
        ["System.WorkItemType"] = El("\"Bug\""),
        ["System.IterationPath"] = El("\"Cobalt\\\\Sprint 3\""),
        ["System.TeamProject"] = El("\"Proj\""),
        ["System.Tags"] = El("\"auth; perf\""),
    });

    [Fact]
    public void A_Work_Item_Row_Renders_Its_Summary_Without_Touching_The_Store()
    {
        var vm = new WorkItemDetailViewModel(new ThrowingWorkItemStore(), ListRow());

        var text = WorkItemDetailFormatter.Render(vm, 60, PreviewTier.Summary);

        Assert.Contains("Bug #7", text);
        Assert.Contains("Token cache misses under load", text);
        // The list route carries no description or comments — they read empty until tier 2 lands,
        // rather than throwing on the way in.
        Assert.Contains("(empty)", text);
        Assert.Contains("── Comments (0) ──", text);
        Assert.False(vm.DescriptionLossy);
    }

    [Fact]
    public void A_Seeded_Work_Item_Keeps_Its_Own_Project_For_The_Later_Detail_Fetch()
    {
        // Org scope spans projects, so the row's project — not the context's — must survive the
        // seeding and reach the tier-2 load (H1).
        var vm = new WorkItemDetailViewModel(new ThrowingWorkItemStore(), ListRow());

        Assert.Equal(7, vm.Id);
        Assert.Equal("Proj", vm.Item!.TeamProject);
    }

    [Fact]
    public void A_Pull_Request_Row_Renders_Its_Summary_From_The_Row_Alone()
    {
        // The PR list route carries the whole PR, so tier 1 is already rich; threads and policies
        // are the parts only the fetch knows.
        var row = new PullRequest(
            PullRequestId: 12,
            Title: "Fix the token stampede",
            Description: null,
            Status: "active",
            IsDraft: false,
            SourceBranch: "feature/x",
            TargetBranch: "main",
            MergeStatus: "succeeded",
            Author: "Jin Puah",
            RepositoryId: "r",
            RepositoryName: "web",
            Reviewers: [new PrReviewer("rid", "Alice Anderson", PrVote.Approved, IsRequired: true)],
            LinkedWorkItemIds: [],
            LastMergeSourceCommitId: "abc",
            ProjectName: "Proj",
            ProjectId: "p");
        var vm = new PrDetailViewModel(null!, row); // a store call would NRE — tier 1 makes none

        var text = PrDetailFormatter.Render(vm, 60, PreviewTier.Summary);

        Assert.Contains("!12  Fix the token stampede", text);
        Assert.Contains("✓ Alice Anderson (required)", text);
        Assert.Contains("── Threads (0 unresolved) ──", text);
    }
}
