using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrDiffViewModelTests
{
    private sealed class FakeDiffSource : IPrDiffSource
    {
        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();
        public IReadOnlyList<PrThread> Threads { get; set; } = [];
        public (string path, int line, bool right, string text)? LastComment { get; private set; }
        public string? LastProject { get; private set; }
        public (string project, string repo, int prId, int threadId, string text)? LastReply { get; private set; }
        public (string project, string repo, int prId, int threadId, PrThreadStatus status)? LastStatusChange { get; private set; }
        public (string project, string repo, int prId, PrVote vote)? LastVote { get; private set; }

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct)
        {
            LastProject = project;
            return Task.FromResult(Iteration);
        }

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct)
        {
            LastProject = project;
            LastComment = (path, line, right, text);
            return Task.CompletedTask;
        }

        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct)
        {
            LastReply = (project, repo, prId, threadId, text);
            return Task.CompletedTask;
        }

        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct)
        {
            LastStatusChange = (project, repo, prId, threadId, status);
            return Task.CompletedTask;
        }

        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct)
        {
            LastVote = (project, repo, prId, vote);
            return Task.CompletedTask;
        }
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src",
            "Contoso.Web");

    [Fact]
    public async Task Load_Lists_Changed_Files_And_Selects_First()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "base")] = "old\n";
        source.Blobs[("/a.cs", "src")] = "new\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("/a.cs", vm.SelectedFile?.Path);
        Assert.NotNull(vm.CurrentDiff);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task PrefetchAllDiffs_Raises_StatsChanged_Per_File_And_Not_Content_Changed()
    {
        // The background stats prefetch computes every file's diff into the cache but never
        // changes the selected file / CurrentDiff, so the displayed diff content is unchanged.
        // It must signal StatsChanged (title totals + file-row stats) rather than Changed (a
        // full diff re-render), so the open file is not re-tokenized once per file in the PR.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "y\n";
        source.Blobs[("/b.cs", "src")] = "y\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var changed = 0;
        var stats = 0;
        vm.Changed += () => changed++;
        vm.StatsChanged += () => stats++;

        await vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(vm.Files.Count, stats);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Added_File_Diffs_Empty_Base_Against_Source()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/new.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/new.cs", "src")] = "a\nb\n"; // base returns "" (not in Blobs)
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Deleted_File_Diffs_Base_Against_Empty_Source()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/gone.cs", FileChangeKind.Delete)],
        };
        source.Blobs[("/gone.cs", "base")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.CurrentDiff!.Deletions);
    }

    [Fact]
    public async Task SelectFile_Recomputes_Current_Diff()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal("/b.cs", vm.SelectedFile?.Path);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Existing_Threads_Map_To_Their_File_And_Line()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(1, "Sam", "elsewhere", false)], "/other.cs", 5, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\ny\n";
        source.Blobs[("/a.cs", "src")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var onLine2 = vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Context, 2, 2, "y"));
        Assert.Single(onLine2);
        Assert.Equal(1, onLine2[0].Id);
        Assert.Empty(vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Context, 99, 99, "z")));
    }

    [Fact]
    public async Task ThreadsByIds_Returns_Matches_In_Requested_Order_Skipping_Absent()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "one", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "two", false)], "/a.cs", 3, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\ny\nz\n";
        source.Blobs[("/a.cs", "src")] = "x\ny\nz\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var picked = vm.ThreadsByIds([2, 1, 99]); // 99 is not a current thread

        Assert.Equal([2, 1], picked.Select(t => t.Id).ToArray()); // requested order kept, absent id dropped
    }

    [Fact]
    public async Task Left_Side_Threads_Map_To_Removed_Lines()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(3, PrThreadStatus.Active, [new PrComment(1, "Sam", "deleted line note", false)], "/a.cs", null, 4),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "a\nb\nc\nd\n";
        source.Blobs[("/a.cs", "src")] = "a\nb\nc\nd\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var onRemoved = vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Removed, 4, null, "d"));

        Assert.Single(onRemoved);
        Assert.Equal(3, onRemoved[0].Id);
    }

    [Fact]
    public async Task AddComment_On_Added_Line_Uses_Right_Side()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // find the added line's index in the diff
        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);
        await vm.AddCommentAtLineAsync(addedIndex, "nice", TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastComment);
        Assert.Equal("/a.cs", source.LastComment!.Value.path);
        Assert.True(source.LastComment.Value.right);
        Assert.Equal(2, source.LastComment.Value.line); // new line number
    }

    [Fact]
    public async Task Renamed_File_Diffs_Base_At_Old_Path()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/new.cs", FileChangeKind.Rename, "/old.cs")],
        };
        source.Blobs[("/old.cs", "base")] = "a\nb\n";
        source.Blobs[("/new.cs", "src")] = "a\nB\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, vm.CurrentDiff!.Additions);
        Assert.Equal(1, vm.CurrentDiff!.Deletions);
    }

    [Fact]
    public async Task Rename_Without_Original_Path_Uses_New_Path()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/moved.cs", FileChangeKind.Rename)],
        };
        source.Blobs[("/moved.cs", "base")] = "a\nb\n";
        source.Blobs[("/moved.cs", "src")] = "a\nB\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, vm.CurrentDiff!.Additions);
        Assert.Equal(1, vm.CurrentDiff!.Deletions);
    }

    [Fact]
    public async Task Diff_Source_Calls_Carry_The_Prs_Own_Project()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Contoso.Web", source.LastProject);
    }

    [Fact]
    public async Task No_Iteration_Sets_Friendly_Error()
    {
        var source = new FakeDiffSource { Iteration = null };
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }

    private sealed class ThrowingSource(Exception ex) : IPrDiffSource
    {
        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromException<PrIteration?>(ex);
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileChange>>([]);
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult("");
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Load_Expected_Failure_Surfaces_Error()
    {
        var vm = new PrDiffViewModel(new ThrowingSource(new HttpRequestException("down")), Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
        Assert.Contains("down", vm.Error);
    }

    [Fact]
    public async Task Load_User_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var vm = new PrDiffViewModel(new ThrowingSource(new OperationCanceledException(cts.Token)), Pr());

        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task Load_Timeout_Cancellation_Surfaces_As_Error()
    {
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var vm = new PrDiffViewModel(new ThrowingSource(new OperationCanceledException(foreign.Token)), Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Load_Unexpected_Exception_Propagates()
    {
        var vm = new PrDiffViewModel(new ThrowingSource(new InvalidOperationException("bug")), Pr());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplyToThread_Calls_Source_And_Refreshes_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Source is refreshed after the reply; simulate the server reflecting the new comment.
        source.Threads =
        [
            new PrThread(1, PrThreadStatus.Active,
                [new PrComment(1, "Sam", "here", false), new PrComment(2, "Jin", "reply", false)], "/a.cs", 2, null),
        ];

        await vm.ReplyToThreadAsync(1, "reply", TestContext.Current.CancellationToken);

        Assert.Equal("Contoso.Web", source.LastReply?.project);
        Assert.Equal("repo-1", source.LastReply?.repo);
        Assert.Equal(10, source.LastReply?.prId);
        Assert.Equal(1, source.LastReply?.threadId);
        Assert.Equal("reply", source.LastReply?.text);
        Assert.Equal(2, vm.Threads[0].Comments.Count);
    }

    [Fact]
    public async Task ResolveThread_Sets_Fixed_Status_And_Refreshes_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        source.Threads = [new PrThread(1, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null)];

        await vm.ResolveThreadAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.LastStatusChange?.threadId);
        Assert.Equal(PrThreadStatus.Fixed, source.LastStatusChange?.status);
        Assert.Equal(PrThreadStatus.Fixed, vm.Threads[0].Status);
    }

    [Fact]
    public async Task ReactivateThread_Sets_Active_Status_And_Refreshes_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        source.Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null)];

        await vm.ReactivateThreadAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.LastStatusChange?.threadId);
        Assert.Equal(PrThreadStatus.Active, source.LastStatusChange?.status);
        Assert.Equal(PrThreadStatus.Active, vm.Threads[0].Status);
    }

    [Fact]
    public async Task Vote_Calls_Source_With_The_Prs_Identity_And_Chosen_Vote()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.VoteAsync(PrVote.Approved, TestContext.Current.CancellationToken);

        Assert.Equal("Contoso.Web", source.LastVote?.project);
        Assert.Equal("repo-1", source.LastVote?.repo);
        Assert.Equal(10, source.LastVote?.prId);
        Assert.Equal(PrVote.Approved, source.LastVote?.vote);
    }

    [Fact]
    public async Task UnresolvedThreadCount_Counts_Only_Active_Non_System_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "resolved", false)], "/a.cs", 3, null),
                new PrThread(3, PrThreadStatus.Active, [new PrComment(1, "System", "system note", true)], "/a.cs", 4, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, vm.UnresolvedThreadCount);
    }

    [Fact]
    public async Task FilteredFiles_Defaults_To_All_Files_And_Leaves_Files_Untouched()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        source.Blobs[("/b.cs", "base")] = "y\n";
        source.Blobs[("/b.cs", "src")] = "y\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.OnlyUnresolvedFiles);
        Assert.Equal(2, vm.FilteredFiles.Count);
        Assert.Equal(2, vm.Files.Count);
    }

    [Fact]
    public async Task FilteredFiles_Keeps_Only_Files_With_An_Unresolved_Thread_When_Toggled_On()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "resolved", false)], "/b.cs", 3, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        source.Blobs[("/b.cs", "base")] = "y\n";
        source.Blobs[("/b.cs", "src")] = "y\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        vm.OnlyUnresolvedFiles = true;

        Assert.Single(vm.FilteredFiles);
        Assert.Equal("/a.cs", vm.FilteredFiles[0].Path);
        // Files (used for index-based navigation) must stay untouched.
        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("/a.cs", vm.Files[0].Path);
        Assert.Equal("/b.cs", vm.Files[1].Path);
    }

    [Fact]
    public async Task MarkViewed_Tracks_Viewed_Files_By_Path()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        source.Blobs[("/b.cs", "base")] = "y\n";
        source.Blobs[("/b.cs", "src")] = "y\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsViewed("/a.cs"));

        vm.MarkViewed("/a.cs");

        Assert.True(vm.IsViewed("/a.cs"));
        Assert.False(vm.IsViewed("/b.cs"));
    }

    [Fact]
    public async Task StatsFor_Returns_Null_Until_The_Files_Diff_Is_Computed()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n3\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(vm.StatsFor("/b.cs"));
        Assert.Equal((1, 0), vm.StatsFor("/a.cs"));

        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal((2, 0), vm.StatsFor("/b.cs"));
    }

    [Fact]
    public async Task PrefetchAllDiffsAsync_Computes_Every_Files_Diff_And_Raises_Changed()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n3\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var changedCount = 0;
        vm.Changed += () => changedCount++;

        await vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), vm.StatsFor("/a.cs"));
        Assert.Equal((2, 0), vm.StatsFor("/b.cs"));
        Assert.Equal(3, vm.TotalAdditions);
        Assert.Equal(0, vm.TotalDeletions);
        Assert.True(changedCount >= 2);
    }

    [Fact]
    public async Task PrefetchAllDiffsAsync_Stops_When_Cancelled()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        // /b.cs blobs deliberately omitted: if the loop reached it after cancellation, this would
        // silently succeed with empty text instead of the test failing loudly.
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        vm.Changed += () => cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.PrefetchAllDiffsAsync(cts.Token));

        Assert.NotNull(vm.StatsFor("/a.cs"));
        Assert.Null(vm.StatsFor("/b.cs"));
    }

    private sealed class OneFileFailsSource : IPrDiffSource
    {
        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileChange>>([new("/good.cs", FileChangeKind.Edit), new("/bad.cs", FileChangeKind.Edit)]);
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            path.Contains("bad") ? Task.FromException<string>(new HttpRequestException("404")) : Task.FromResult("a\nb\n");
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task PrefetchAllDiffs_Skips_A_Failing_File_And_Continues()
    {
        var vm = new PrDiffViewModel(new OneFileFailsSource(), Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // /bad.cs throws an expected (404-style) error; the prefetch must skip it, not abort/crash.
        await vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.StatsFor("/good.cs"));
        Assert.Null(vm.StatsFor("/bad.cs"));
    }
}
