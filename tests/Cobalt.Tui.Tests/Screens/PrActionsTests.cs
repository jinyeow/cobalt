using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// Unit-tests the shared PR vote runner (the list entry point) with a fake store,
/// injected chooser, and inline UI-post. Guards that the vote reaches the
/// view-model and carries the PR's own project through (cross-project drill-in).
/// </summary>
public class PrActionsTests
{
    private static readonly IApplication App = Application.Create();

    private static PullRequest Pr() =>
        new(10, "Add feature", "desc", "active", false, "feature", "main", "succeeded",
            "Jin", "repo-1", "web", [], [], "abc123", "Contoso.Web");

    private sealed class FakeStore : IPullRequestStore
    {
        public PrVote? VotedValue { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }

        public Task<PullRequest> GetPullRequestAsync(int id, CancellationToken ct) => Task.FromResult(Pr());
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task VoteAsync(string project, string repo, int id, PrVote vote, CancellationToken ct)
        {
            LastProject = project;
            LastRepository = repo;
            VotedValue = vote;
            return Task.CompletedTask;
        }
        public Task ReplyToThreadAsync(string project, string repo, int id, int threadId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int id, int threadId, PrThreadStatus status, CancellationToken ct) => Task.CompletedTask;
        public Task AbandonAsync(string project, string repo, int id, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string project, string repo, int id, string mergeStrategy, bool deleteSource, CancellationToken ct) => Task.CompletedTask;
    }

    private static PrActions Actions(int? choice) =>
        new(App, _ => { }, choose: (_, _) => choice, post: a => a());

    [Fact]
    public async Task RunVote_Sends_Chosen_Vote_With_Prs_Own_Project()
    {
        var store = new FakeStore();
        var actions = Actions(choice: 0); // index 0 → approve

        await actions.RunVoteAsync(store, 10, TestContext.Current.CancellationToken);

        Assert.Equal(PrVote.Approved, store.VotedValue);
        Assert.Equal("Contoso.Web", store.LastProject);
        Assert.Equal("repo-1", store.LastRepository);
    }

    [Fact]
    public async Task RunVote_Dismissed_Chooser_Sends_No_Vote()
    {
        var store = new FakeStore();
        var actions = Actions(choice: null);

        await actions.RunVoteAsync(store, 10, TestContext.Current.CancellationToken);

        Assert.Null(store.VotedValue);
    }
}
