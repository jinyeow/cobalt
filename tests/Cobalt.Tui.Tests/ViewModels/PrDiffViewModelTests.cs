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

    /// <summary>
    /// A diff source whose blob fetches block until <see cref="Release"/>, so a test can observe how
    /// many blob requests are in flight at once rather than only the end result.
    /// </summary>
    private sealed class GatedBlobSource : IPrDiffSource
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _issued = new(0);

        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Iteration);

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public async Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct)
        {
            _issued.Release();
            await _release.Task.ConfigureAwait(false);
            return Blobs.GetValueOrDefault((path, commit), "");
        }

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

        /// <summary>True once <paramref name="count"/> blob requests have been issued; false on timeout.</summary>
        public async Task<bool> WaitForBlobRequestsAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    await _issued.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Release() => _release.TrySetResult();
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
    public async Task Edited_File_Fetches_Base_And_Source_Blobs_Concurrently()
    {
        var source = new GatedBlobSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());

        var load = vm.LoadAsync(TestContext.Current.CancellationToken);

        // The two blobs are independent: awaiting them in sequence leaves the source blob unissued
        // until the base blob lands, costing a whole round-trip on every cache miss.
        Assert.True(
            await source.WaitForBlobRequestsAsync(2, TimeSpan.FromSeconds(5)),
            "both blob requests must be in flight before either is released");

        source.Release();
        await load;

        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Added_File_Fetches_Only_The_Source_Blob()
    {
        var source = new GatedBlobSource
        {
            Changes = [new FileChange("/new.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/new.cs", "src")] = "a\nb\n";
        var vm = new PrDiffViewModel(source, Pr());

        var load = vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(await source.WaitForBlobRequestsAsync(1, TimeSpan.FromSeconds(5)));
        source.Release();
        await load;

        // An added file has no base version: the short-circuit must survive parallelising the pair,
        // so a second request must never be issued.
        Assert.False(await source.WaitForBlobRequestsAsync(1, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(2, vm.CurrentDiff!.Additions);
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
    public async Task CurrentDiffPath_Tracks_The_File_CurrentDiff_Belongs_To()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/a.cs", vm.CurrentDiffPath);

        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        // The path must name the file CurrentDiff was actually computed from: the view pairs the
        // two to render the header, so a drift shows one file's path with another's stats.
        Assert.Equal("/b.cs", vm.CurrentDiffPath);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task CurrentDiffPath_Is_Null_When_There_Is_No_Diff()
    {
        var source = new FakeDiffSource { Iteration = null };
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(vm.CurrentDiff);
        Assert.Null(vm.CurrentDiffPath);
    }

    [Fact]
    public async Task CommentedLinesFor_Splits_Thread_Lines_By_Side_For_One_File()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "right", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(1, "Sam", "right too", false)], "/a.cs", 7, null),
                new PrThread(3, PrThreadStatus.Active, [new PrComment(1, "Sam", "left", false)], "/a.cs", null, 4),
                new PrThread(4, PrThreadStatus.Active, [new PrComment(1, "Sam", "other file", false)], "/other.cs", 9, null),
                new PrThread(5, PrThreadStatus.Active, [new PrComment(1, "Sam", "no anchor", false)], "/a.cs", null, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var (left, right) = vm.CommentedLinesFor("/a.cs");

        Assert.Equal([4], left.Order());
        Assert.Equal([2, 7], right.Order());
        // Another file's threads never leak in.
        var (otherLeft, otherRight) = vm.CommentedLinesFor("/other.cs");
        Assert.Empty(otherLeft);
        Assert.Equal([9], otherRight.Order());
    }

    [Fact]
    public async Task CommentedLinesFor_Counts_Resolved_And_System_Threads_Like_ThreadsForDiffLine()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "resolved", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(1, "System", "system", true)], "/a.cs", 3, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var (_, right) = vm.CommentedLinesFor("/a.cs");

        // ThreadsForDiffLine does no status/system filtering, so the marker sets must not either.
        Assert.Equal([2, 3], right.Order());
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
    public async Task PrefetchAllDiffsAsync_Computes_Every_Files_Diff_And_Raises_StatsChanged()
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

        var statsCount = 0;
        var changedCount = 0;
        vm.StatsChanged += () => statsCount++;
        vm.Changed += () => changedCount++;

        await vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), vm.StatsFor("/a.cs"));
        Assert.Equal((2, 0), vm.StatsFor("/b.cs"));
        Assert.Equal(3, vm.TotalAdditions);
        Assert.Equal(0, vm.TotalDeletions);
        Assert.True(statsCount >= 2);
        // Prefetch never changes the selected file, so it must not raise the content-level
        // Changed (which would re-tokenize the open diff once per file in the PR).
        Assert.Equal(0, changedCount);
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
        // Prefetch signals each computed file via StatsChanged; cancel after the first so the
        // loop must stop before /b.cs (whose blobs are omitted above).
        vm.StatsChanged += () => cts.Cancel();

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
