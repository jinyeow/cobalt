using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The diff dialog's view-state half, unit-tested without a terminal (ADR 0004). These cover the
/// state machines the 68 headless key tests could only reach through the widget tree.
///
/// Identity discipline, the thing this file exists to pin: everything here is DISPLAYED-side —
/// keyed on the diff actually on screen (<c>CurrentDiff</c> / <c>CurrentDiffSnapshot</c>), never on
/// the file-tree cursor (<c>SelectedFile</c>), which during a select has already moved to the next
/// file. See the class doc on <see cref="DiffReviewViewModel"/>.
/// </summary>
public class DiffReviewViewModelTests
{
    private sealed class FakeDiffSource : IPrDiffSource
    {
        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();
        public IReadOnlyList<PrThread> Threads { get; set; } = [];

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Iteration);

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;

        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;

        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src", "Contoso.Web");

    /// <summary>
    /// A loaded diff VM with one file on screen whose added lines are "alpha/beta/alpha/gamma",
    /// so "alpha" has two matches and "beta" one.
    /// </summary>
    private static async Task<PrDiffViewModel> LoadedVm()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "alpha\nbeta\nalpha\ngamma\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        await vm.SelectFileAsync(0, TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task Applying_A_Search_Reports_The_First_Match_To_Jump_To()
    {
        var review = new DiffReviewViewModel(await LoadedVm());

        var outcome = review.ApplySearch("alpha");

        Assert.Equal(2, review.MatchCount);
        Assert.NotNull(outcome.JumpToLine);
        Assert.Null(outcome.NoMatchesFor);
        // The jump target is the first match's line, not row 0 — the caller selects it verbatim.
        Assert.Equal(review.Matches[0].LineIndex, outcome.JumpToLine);
    }

    [Fact]
    public async Task A_Search_With_No_Matches_Reports_The_Query_Instead_Of_A_Jump()
    {
        // The dialog logs "no matches for X" off this; a jump of 0 would silently move the cursor
        // to the top of the file instead, which is what makes these two outcomes distinct fields.
        var review = new DiffReviewViewModel(await LoadedVm());

        var outcome = review.ApplySearch("nothing-here");

        Assert.Equal(0, review.MatchCount);
        Assert.Null(outcome.JumpToLine);
        Assert.Equal("nothing-here", outcome.NoMatchesFor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_Blank_Query_Clears_The_Search_Without_Jumping_Or_Logging(string? query)
    {
        var review = new DiffReviewViewModel(await LoadedVm());
        review.ApplySearch("alpha");

        var outcome = review.ApplySearch(query);

        Assert.Equal(0, review.MatchCount);
        Assert.Null(outcome.JumpToLine);
        Assert.Null(outcome.NoMatchesFor);
    }

    [Fact]
    public async Task The_Query_Is_Trimmed_Before_Searching_And_When_Reported()
    {
        var review = new DiffReviewViewModel(await LoadedVm());

        var outcome = review.ApplySearch("  no-such-text  ");

        Assert.Equal("no-such-text", outcome.NoMatchesFor);
    }

    [Fact]
    public async Task Stepping_Forward_Walks_The_Matches_And_Wraps()
    {
        var review = new DiffReviewViewModel(await LoadedVm());
        review.ApplySearch("alpha"); // two matches, index parked on the first

        var second = review.StepSearch(forward: true);
        var wrapped = review.StepSearch(forward: true);

        Assert.Equal(review.Matches[1].LineIndex, second);
        Assert.Equal(review.Matches[0].LineIndex, wrapped);
    }

    [Fact]
    public async Task Stepping_Backward_From_The_First_Match_Wraps_To_The_Last()
    {
        var review = new DiffReviewViewModel(await LoadedVm());
        review.ApplySearch("alpha");

        var back = review.StepSearch(forward: false);

        Assert.Equal(review.Matches[1].LineIndex, back);
    }

    [Fact]
    public async Task Stepping_With_No_Active_Search_Reports_Nowhere_To_Go()
    {
        // n/N before any '/' must be inert rather than selecting line 0.
        var review = new DiffReviewViewModel(await LoadedVm());

        Assert.Null(review.StepSearch(forward: true));
        Assert.Null(review.StepSearch(forward: false));
    }

    [Fact]
    public async Task Clearing_The_Search_Drops_The_Matches()
    {
        // Match line-indexes are scoped to one file, so the render path clears them the moment the
        // displayed file changes; keeping them would decorate the new file at the old file's lines.
        var review = new DiffReviewViewModel(await LoadedVm());
        review.ApplySearch("alpha");
        Assert.Equal(2, review.MatchCount);

        review.ClearSearch();

        Assert.Equal(0, review.MatchCount);
        Assert.Empty(review.Matches);
    }

    [Fact]
    public async Task Matches_Come_From_The_Displayed_Diff_Not_The_Tree_Cursor()
    {
        // The identity rule in one assertion. The cursor is moved to a second file whose blob fetch
        // never lands, so SelectedFile is /b.cs while /a.cs is still displayed. A search must find
        // /a.cs's text — searching the cursor's file would find nothing (and a later select would
        // decorate the wrong file's lines).
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "src")] = "alpha\nbeta\n";
        source.Blobs[("/b.cs", "src")] = "delta\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        await vm.SelectFileAsync(0, TestContext.Current.CancellationToken);
        var review = new DiffReviewViewModel(vm);

        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal("/b.cs", vm.CurrentDiffPath);
        Assert.Null(review.ApplySearch("alpha").JumpToLine);    // /a.cs's text is off screen now
        Assert.NotNull(review.ApplySearch("delta").JumpToLine); // the displayed file is what's searched
    }
}
