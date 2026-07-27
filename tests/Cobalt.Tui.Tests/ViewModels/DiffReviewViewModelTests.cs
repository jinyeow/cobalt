using Cobalt.Core.Models;
using Cobalt.Core.Text;
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

    // ---- pane composition ----------------------------------------------------------------
    //
    // ComposePane is the whole of the old Render diff-pane block: same-file detection, the
    // search-state drop, fold reuse, the compose call, and the keep-line decision. The dialog is
    // left assigning Source/SelectedItem from what it returns.

    // A change at the top then 14 identical context lines: the trailing context run folds, so the
    // unified projection has exactly one fold to expand (mirrors DiffPaneComposerTests).
    private static FileDiff FoldingDiff()
    {
        var ctx = string.Join("\n", Enumerable.Range(0, 14).Select(i => $"ctx {i}"));
        return DiffService.Unified("old\n" + ctx + "\n", "new\n" + ctx + "\n");
    }

    /// <summary>A view-model with no files, for tests that drive ComposePane with a hand-built diff.</summary>
    private static async Task<PrDiffViewModel> EmptyVm()
    {
        var vm = new PrDiffViewModel(new FakeDiffSource(), Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task Composing_The_Same_File_Again_Keeps_The_Cursor_Row()
    {
        // A comment or a mark-viewed re-renders the open file; the reviewer's line must not jump
        // back to the top under them.
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        var update = review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: 4);

        Assert.Equal(4, update.SelectedRow);
    }

    [Fact]
    public async Task Composing_A_Different_File_Resets_To_The_Top()
    {
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        // The cursor row is carried in from the widget but must be ignored: row 4 of the old file
        // means nothing in the new one.
        var update = review.ComposePane((diff, "/b.cs"), contentWidth: 120, paneSelectedRow: 4);

        Assert.Equal(0, update.SelectedRow);
    }

    [Fact]
    public async Task The_Kept_Row_Is_Clamped_To_The_New_Row_Count()
    {
        // A same-file recompose can shrink the pane (a fold re-collapsing, a narrower width), and
        // an out-of-range SelectedItem would throw at the widget.
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        var first = review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        var update = review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: 9_999);

        Assert.Equal(first.Styled.Count - 1, update.SelectedRow);
    }

    [Fact]
    public async Task An_Empty_Diff_Reports_No_Row_To_Select()
    {
        // Nothing to select rather than row 0 — the dialog skips the SelectedItem write entirely,
        // as it always has.
        var review = new DiffReviewViewModel(await EmptyVm());

        var update = review.ComposePane((DiffService.Unified("", ""), "/a.cs"), contentWidth: 120, paneSelectedRow: 3);

        Assert.Empty(update.Styled);
        Assert.Null(update.SelectedRow);
    }

    [Fact]
    public async Task Composing_A_Different_File_Drops_The_Search()
    {
        // Match line-indexes belong to one file. Carrying them over would paint the new file's
        // lines as hits at the old file's positions.
        var vm = await LoadedVm();
        var review = new DiffReviewViewModel(vm);
        review.ComposePane((vm.CurrentDiff!, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        review.ApplySearch("alpha");
        Assert.Equal(2, review.MatchCount);

        review.ComposePane((vm.CurrentDiff!, "/b.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.Equal(0, review.MatchCount);
    }

    [Fact]
    public async Task Composing_The_Same_File_Again_Keeps_The_Search()
    {
        var vm = await LoadedVm();
        var review = new DiffReviewViewModel(vm);
        review.ComposePane((vm.CurrentDiff!, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        review.ApplySearch("alpha");

        review.ComposePane((vm.CurrentDiff!, "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.Equal(2, review.MatchCount);
    }

    [Fact]
    public async Task An_Expanded_Fold_Survives_A_Same_File_Recompose()
    {
        // e/E expansions must outlive the re-render a comment or mark-viewed triggers.
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        var folded = review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        Assert.Contains(review.DiffRows, r => r.FoldId is not null); // the fixture really folds
        Assert.True(review.ExpandAllFolds());

        var expanded = review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.DoesNotContain(review.DiffRows, r => r.FoldId is not null);
        Assert.True(expanded.Styled.Count > folded.Styled.Count); // the hidden context is now shown
    }

    [Fact]
    public async Task Fold_State_Is_Rebuilt_When_The_Displayed_File_Changes()
    {
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        review.ExpandAllFolds();

        review.ComposePane((diff, "/b.cs"), contentWidth: 120, paneSelectedRow: null);

        // A fresh file starts folded again, however the previous one was left.
        Assert.Contains(review.DiffRows, r => r.FoldId is not null);
    }

    [Fact]
    public async Task Forcing_Unified_Is_Sticky_Until_The_User_Toggles_Back()
    {
        // The narrow-width force is one-way by design: widening the dialog does not silently
        // restore side-by-side, and nothing but 's' does. ForceUnified is deliberately not a
        // setter for that reason.
        var review = new DiffReviewViewModel(await EmptyVm());
        Assert.True(review.ToggleMode()); // 's' → side-by-side

        review.ForceUnified();

        Assert.False(review.SideBySide);
        review.ForceUnified(); // a second narrow render changes nothing
        Assert.False(review.SideBySide);
        Assert.True(review.ToggleMode()); // only the user's 's' brings it back
    }

    [Fact]
    public async Task Toggling_The_Mode_Reports_The_Mode_It_Landed_In()
    {
        // The dialog logs off the return value rather than re-reading the field.
        var review = new DiffReviewViewModel(await EmptyVm());

        Assert.True(review.ToggleMode());
        Assert.False(review.ToggleMode());
    }

    [Fact]
    public async Task Side_By_Side_Carries_No_Folds_To_Expand()
    {
        // The composer returns no fold state in side-by-side (full context is shown), so e/E are
        // inert there and must not trigger a re-render.
        var review = new DiffReviewViewModel(await EmptyVm());
        review.ToggleMode();
        review.ComposePane((FoldingDiff(), "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.False(review.ExpandAllFolds());
        Assert.False(review.ExpandFoldAt(0));
    }

    [Fact]
    public async Task Expanding_At_The_Cursor_Falls_Back_To_The_First_Fold()
    {
        // 'e' with the cursor on an ordinary line expands the first collapsed fold rather than
        // doing nothing.
        var review = new DiffReviewViewModel(await EmptyVm());
        review.ComposePane((FoldingDiff(), "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.True(review.ExpandFoldAt(0)); // row 0 is a changed line, not the fold marker
    }

    [Fact]
    public async Task Expanding_Reports_Nothing_To_Do_When_There_Are_No_Folds_Left()
    {
        var review = new DiffReviewViewModel(await EmptyVm());
        review.ComposePane((FoldingDiff(), "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        review.ExpandAllFolds();
        review.ComposePane((FoldingDiff(), "/a.cs"), contentWidth: 120, paneSelectedRow: null);

        Assert.False(review.ExpandFoldAt(0)); // nothing collapsed remains
    }

    [Fact]
    public async Task Revealing_A_Hidden_Line_Expands_The_Fold_Containing_It()
    {
        // n/N and hunk/thread nav land on lines the fold hides; the caller re-renders only when
        // this reports that it changed the fold state.
        var review = new DiffReviewViewModel(await EmptyVm());
        var diff = FoldingDiff();
        review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        var hidden = Enumerable.Range(0, diff.Lines.Count).First(i => !review.IsLineVisible(i));

        Assert.True(review.RevealLine(hidden));

        review.ComposePane((diff, "/a.cs"), contentWidth: 120, paneSelectedRow: null);
        Assert.True(review.IsLineVisible(hidden));
        Assert.False(review.RevealLine(hidden)); // already visible: no re-render needed
    }
}
