using System.Text.Json;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// Representative loaded detail view-models shared by the dialog render snapshot tests
/// and the formatter tests, so both assert against the exact same state (ADR 0024:
/// dialog and preview must share one text composition).
/// </summary>
internal static class DetailFormatterFixture
{
    // ---- PR ----

    internal sealed class PrStore : IPullRequestStore
    {
        public PullRequest Pr { get; set; } = new(
            42,
            "Add retry to token cache",
            "Adds a bounded retry to the token cache.\n\nMotivation: transient AAD hiccups\nshould not surface as auth failures.\nSee ADR 0013 for the error policy.",
            "active",
            IsDraft: false,
            "feature/token-retry",
            "main",
            "succeeded",
            "Jin Puah",
            "repo-guid",
            "cobalt",
            Reviewers:
            [
                new PrReviewer("r1", "Alice Anderson", PrVote.Approved, IsRequired: true),
                new PrReviewer("r2", "Bob Brown", PrVote.WaitingForAuthor, IsRequired: false),
                new PrReviewer("r3", "Carol Clark", PrVote.NoVote, IsRequired: false),
            ],
            LinkedWorkItemIds: [101, 202],
            LastMergeSourceCommitId: "abc123",
            ProjectName: "Proj",
            ProjectId: "proj-guid");

        public IReadOnlyList<PrThread> Threads { get; set; } =
        [
            new PrThread(1, PrThreadStatus.Active,
                [
                    new PrComment(10, "Alice Anderson", "Should this retry be capped?", IsSystem: false),
                    new PrComment(11, "Jin Puah", "Yes — capped at 3, see the constant.", IsSystem: false),
                    new PrComment(12, "system", "Alice Anderson voted 10", IsSystem: true),
                ],
                "src/TokenCache.cs", RightLine: 34, LeftLine: null),
            new PrThread(2, PrThreadStatus.Fixed,
                [new PrComment(20, "Bob Brown", "typo in the log message", IsSystem: false)],
                FilePath: null, RightLine: null, LeftLine: null),
        ];

        public IReadOnlyList<PolicyEvaluation> Policies { get; set; } =
        [
            new PolicyEvaluation("Build validation", "approved", IsBlocking: true),
            new PolicyEvaluation("Minimum reviewers", "rejected", IsBlocking: true),
            new PolicyEvaluation("Comment resolution", "queued", IsBlocking: false),
        ];

        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) => Task.FromResult(Pr);
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int id, CancellationToken ct) =>
            Task.FromResult(Threads);
        public Task<IReadOnlyList<PolicyEvaluation>> GetPolicyEvaluationsAsync(string project, int id, CancellationToken ct) =>
            Task.FromResult(Policies);
        public Task VoteAsync(string project, string repositoryId, int id, PrVote vote, CancellationToken ct) => Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repositoryId, int id, int threadId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repositoryId, int id, int threadId, PrThreadStatus status, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonAsync(string project, string repositoryId, int id, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string project, string repositoryId, int id, string mergeStrategy, bool deleteSource, CancellationToken ct) => Task.CompletedTask;
        public Task AddPrCommentAsync(string project, string repositoryId, int id, string text, CancellationToken ct) => Task.CompletedTask;
    }

    internal static async Task<PrDetailViewModel> LoadedPrVmAsync(CancellationToken ct, Action<PrStore>? mutate = null)
    {
        var store = new PrStore();
        mutate?.Invoke(store);
        var vm = new PrDetailViewModel(store, 42);
        await vm.LoadAsync(ct);
        return vm;
    }

    // ---- work item ----

    internal sealed class WorkItemStore : IWorkItemStore
    {
        public WorkItem Item { get; set; } = new(7, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = El("\"Token cache misses under load\""),
            ["System.State"] = El("\"Active\""),
            ["System.WorkItemType"] = El("\"Bug\""),
            ["System.IterationPath"] = El("\"Cobalt\\\\Sprint 3\""),
            ["System.TeamProject"] = El("\"Proj\""),
            ["System.Tags"] = El("\"auth; perf\""),
            ["System.AssignedTo"] = El("{\"displayName\":\"Jin Puah\",\"uniqueName\":\"jin@contoso.com\"}"),
            ["Microsoft.VSTS.Common.Priority"] = El("2"),
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = El("5"),
            ["System.Description"] = El(
                "\"<p>Cache misses spike when tokens expire.</p><p>Repro: run the soak harness for an hour.</p><p>Expected: one refresh per expiry.</p><p>Actual: a refresh stampede.</p><p>Suspect the lock scope in TokenCache.</p>\""),
        });

        public IReadOnlyList<WorkItemComment> Comments { get; set; } =
        [
            new WorkItemComment(1, "Alice Anderson", new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero), "Repro confirmed on the soak rig."),
            new WorkItemComment(2, "Bob Brown", new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero), "Lock scope looks per-call, not per-key."),
            new WorkItemComment(3, "Jin Puah", new DateTimeOffset(2026, 7, 3, 11, 0, 0, TimeSpan.Zero), "Fix drafted; needs a stress test."),
        ];

        public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) => Task.FromResult(Item);
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) =>
            Task.FromResult(Comments);
        public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItemStateDto>>([new() { Name = "Active" }]);
        public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) =>
            Task.FromResult(Item);
        public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) =>
            Task.FromResult(new WorkItemComment(99, "me", DateTimeOffset.UnixEpoch, text));
    }

    internal static async Task<WorkItemDetailViewModel> LoadedWorkItemVmAsync(CancellationToken ct, Action<WorkItemStore>? mutate = null)
    {
        var store = new WorkItemStore();
        mutate?.Invoke(store);
        var vm = new WorkItemDetailViewModel(store, 7);
        await vm.LoadAsync(ct);
        return vm;
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- goldens ----
    // Captured from the pre-extraction dialog RenderBody output for the fixture states
    // above; the snapshot and formatter tests both assert against these verbatim.
    // ReplaceLineEndings guards against a CRLF checkout changing the raw literals.

    internal static readonly string PrFullGolden =
        """
        !42  Add retry to token cache
        cobalt: feature/token-retry → main   status: active   merge: succeeded
        author: Jin Puah

        Reviewers:
          ✓ Alice Anderson (required)
          ⧗ Bob Brown
          · Carol Clark

        Policies:
          ✓ Build validation (blocking)
          ✗ Minimum reviewers (blocking)
          ⧗ Comment resolution

        Linked work items: #101, #202

        ── Description ──
        Adds a bounded retry to the token cache.

        Motivation: transient AAD hiccups
        should not surface as auth failures.
        See ADR 0013 for the error policy.

        ── Threads (1 unresolved) ──
          #1 [Active] [src/TokenCache.cs:34]
              Alice Anderson: Should this retry be capped?
              Jin Puah: Yes — capped at 3, see the constant.
          #2 [Fixed]
              Bob Brown: typo in the log message
        """.ReplaceLineEndings("\n");

    internal static readonly string WorkItemFullGolden =
        """
        Bug #7   [Active]
        Title:    Token cache misses under load
        Assigned: Jin Puah
        Iteration:Cobalt\Sprint 3
        Tags:     auth, perf
        Priority: 2   Points: 5

        ── Description ──
        Cache misses spike when tokens expire.

        Repro: run the soak harness for an hour.

        Expected: one refresh per expiry.

        Actual: a refresh stampede.

        Suspect the lock scope in TokenCache.

        ── Comments (3) ──
          Alice Anderson (2026-07-01): Repro confirmed on the soak rig.
          Bob Brown (2026-07-02): Lock scope looks per-call, not per-key.
          Jin Puah (2026-07-03): Fix drafted; needs a stress test.
        """.ReplaceLineEndings("\n");
}
