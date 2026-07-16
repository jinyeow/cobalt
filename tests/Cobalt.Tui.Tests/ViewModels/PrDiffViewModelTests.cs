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

        /// <summary>When set, every threads fetch fails with it — an ADO outage, on load or refresh.</summary>
        public Exception? ThreadsError { get; set; }

        /// <summary>When set, the mutation itself fails with it, before any threads refresh runs.</summary>
        public Exception? MutationError { get; set; }

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            ThreadsError is { } ex
                ? Task.FromException<IReadOnlyList<PrThread>>(ex)
                : Task.FromResult(Threads);

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct)
        {
            if (MutationError is { } ex)
            {
                return Task.FromException(ex);
            }
            LastProject = project;
            LastComment = (path, line, right, text);
            return Task.CompletedTask;
        }

        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct)
        {
            if (MutationError is { } ex)
            {
                return Task.FromException(ex);
            }
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
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blobCalls;

        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();
        public IReadOnlyList<PrThread> Threads { get; set; } = [];
        public (string path, int line, bool right, string text)? LastComment { get; private set; }

        /// <summary>How many blob fetches have been issued (not necessarily completed).</summary>
        public int BlobCalls => Volatile.Read(ref _blobCalls);

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Iteration);

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public async Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct)
        {
            Interlocked.Increment(ref _blobCalls);
            await Volatile.Read(ref _release).Task.ConfigureAwait(false);
            return Blobs.GetValueOrDefault((path, commit), "");
        }

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct)
        {
            LastComment = (path, line, right, text);
            return Task.CompletedTask;
        }
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;

        /// <summary>True once at least <paramref name="count"/> blob fetches have been issued; false on timeout.</summary>
        public async Task<bool> WaitForBlobCallsAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (BlobCalls < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>Lets every blocked and subsequent blob fetch complete.</summary>
        public void Release() => Volatile.Read(ref _release).TrySetResult();

        /// <summary>Blocks blob fetches again, so a later navigation can be caught in flight.</summary>
        public void Rearm() =>
            Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>A diff source whose thread fetch blocks until <see cref="ReleaseThreads"/>; blobs resolve immediately.</summary>
    private sealed class GatedThreadsSource : IPrDiffSource
    {
        private readonly TaskCompletionSource<IReadOnlyList<PrThread>> _threads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            _threads.Task;

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;

        public void ReleaseThreads(IReadOnlyList<PrThread> threads) => _threads.TrySetResult(threads);
        public void FailThreads(Exception ex) => _threads.TrySetException(ex);
    }

    /// <summary>
    /// Records every blob request in order and blocks each until released by path, so a test can
    /// observe which files the prefetch chooses, how many it runs at once, and in what order.
    /// </summary>
    private sealed class OrderedGateSource : IPrDiffSource
    {
        private readonly object _lock = new();
        private readonly List<string> _requested = [];
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

        public IReadOnlyList<FileChange> Changes { get; set; } = [];

        /// <summary>Blob request paths, in the order they were issued.</summary>
        public IReadOnlyList<string> Requested
        {
            get { lock (_lock) { return [.. _requested]; } }
        }

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public async Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct)
        {
            TaskCompletionSource gate;
            lock (_lock)
            {
                _requested.Add(path);
                gate = GateFor(path);
            }
            await gate.Task.ConfigureAwait(false);
            return $"{path} line\n";
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

        public void Release(string path)
        {
            lock (_lock)
            {
                GateFor(path).TrySetResult();
            }
        }

        /// <summary>True once at least <paramref name="count"/> blob requests have been issued.</summary>
        public async Task<bool> WaitForRequestsAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Requested.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            return true;
        }

        // Callers hold _lock.
        private TaskCompletionSource GateFor(string path) =>
            _gates.TryGetValue(path, out var gate)
                ? gate
                : _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Eight added files: one blob request each, so request order is unambiguous.</summary>
    private static FileChange[] EightAddedFiles() =>
        [.. Enumerable.Range(0, 8).Select(i => new FileChange($"/f{i}.cs", FileChangeKind.Add))];

    /// <summary>
    /// Fails /bad.cs's blobs until <see cref="Succeed"/> is set. Unlike <see cref="OneFileFailsSource"/>
    /// (which fails permanently), this can show that a failed fetch leaves nothing behind that would
    /// block a later retry.
    /// </summary>
    private sealed class FailThenSucceedSource : IPrDiffSource
    {
        public bool Succeed { get; set; }

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileChange>>([new("/good.cs", FileChangeKind.Edit), new("/bad.cs", FileChangeKind.Edit)]);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            path.Contains("bad") && !Succeed
                ? Task.FromException<string>(new HttpRequestException("404"))
                : Task.FromResult(commit == "base" ? "a\n" : "a\nb\n");

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

    /// <summary>Threads load cleanly, but the only (first) file's blobs fail.</summary>
    private sealed class FirstFileBlobFailsSource : IPrDiffSource
    {
        public IReadOnlyList<PrThread> Threads { get; set; } = [];

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileChange>>([new("/bad.cs", FileChangeKind.Edit)]);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromException<string>(new HttpRequestException("blob 500"));

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

    /// <summary>True if <paramref name="task"/> completes within <paramref name="timeout"/>.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;

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
            await source.WaitForBlobCallsAsync(2, TimeSpan.FromSeconds(5)),
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

        source.Release();
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // An added file has no base version: the short-circuit must survive parallelising the pair,
        // so only the source blob is ever requested.
        Assert.Equal(1, source.BlobCalls);
        Assert.Equal(2, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Concurrent_Selects_Of_The_Same_File_Share_One_Fetch()
    {
        var source = new GatedBlobSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());

        source.Release();
        await vm.LoadAsync(TestContext.Current.CancellationToken); // warms /a.cs
        source.Rearm();                                            // /b.cs's blobs now block
        var before = source.BlobCalls;

        // Prefetch and user navigation routinely land on the same uncached file at once: the
        // second caller must join the first fetch, not start its own.
        var first = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        var second = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        Assert.True(await source.WaitForBlobCallsAsync(before + 2, TimeSpan.FromSeconds(5)));

        source.Release();
        await Task.WhenAll(first, second);

        // One shared fetch = one base + one source blob, not a pair per caller.
        Assert.Equal(2, source.BlobCalls - before);
        Assert.Equal("/b.cs", vm.CurrentDiffPath);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task A_File_Whose_Fetch_Failed_Is_Retried_On_A_Later_Select()
    {
        var source = new FailThenSucceedSource();
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // The prefetch swallows /bad.cs's failure...
        await vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);
        Assert.Null(vm.StatsFor("/bad.cs"));

        // ...and must not leave a stuck in-flight entry behind: selecting it re-issues the fetch.
        source.Succeed = true;
        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal("/bad.cs", vm.CurrentDiffPath);
        Assert.NotNull(vm.StatsFor("/bad.cs"));
    }

    [Fact]
    public async Task Load_Does_Not_Publish_An_Interactive_Diff_Before_Threads_Arrive()
    {
        var source = new GatedThreadsSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());

        // A paint carrying a finished, interactive diff while Threads is still empty tells the
        // reviewer this file has no comments on a PR that has them — `o` / `]t` report "no thread"
        // with no cue that the threads simply have not arrived yet.
        string? premature = null;
        vm.Changed += () =>
        {
            if (!vm.IsLoading && vm.CurrentDiff is not null && vm.Threads.Count == 0)
            {
                premature = "a Changed published an interactive diff while Threads was still empty";
            }
        };

        var load = vm.LoadAsync(TestContext.Current.CancellationToken);
        source.ReleaseThreads([
            new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null),
        ]);
        await load;

        Assert.Null(premature);
        Assert.Single(vm.Threads);
        Assert.NotNull(vm.CurrentDiff);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Load_Publishes_The_Diff_In_A_Single_Paint()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());

        // Every Changed carrying a diff re-tokenizes the whole file in the view, so opening a PR
        // must cost exactly one such pass rather than one per loading stage.
        var paintsWithADiff = 0;
        vm.Changed += () =>
        {
            if (vm.CurrentDiff is not null)
            {
                paintsWithADiff++;
            }
        };

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, paintsWithADiff);
    }

    [Fact]
    public async Task Load_Threads_Failure_Leaves_CurrentDiff_Null_So_The_Error_Survives()
    {
        var source = new GatedThreadsSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());

        var load = vm.LoadAsync(TestContext.Current.CancellationToken);
        source.FailThreads(new HttpRequestException("threads down"));
        await load;

        Assert.NotNull(vm.Error);
        Assert.Contains("threads down", vm.Error);
        // The view renders Error only while CurrentDiff is null, overwriting the error header with
        // the file's stats otherwise. Publishing a diff here would swallow the failure and show a
        // clean diff with zero markers on a PR that has review comments.
        Assert.Null(vm.CurrentDiff);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Load_Diff_Failure_Still_Populates_Threads()
    {
        var source = new FirstFileBlobFailsSource
        {
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/bad.cs", 2, null)],
        };
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // LoadAsync runs once per dialog, so losing the threads to a first-file diff failure leaves
        // the whole session with no comment markers.
        Assert.Single(vm.Threads);
        Assert.NotNull(vm.Error);
        Assert.Null(vm.CurrentDiff);
        // The threads loaded fine; only the diff failed. Reporting a degraded session here would
        // warn about a condition that does not exist.
        Assert.False(vm.ThreadsUnavailable);
    }

    [Fact]
    public async Task Threads_Failure_Marks_Threads_Unavailable_For_The_Whole_Session()
    {
        var source = new GatedThreadsSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());

        Assert.False(vm.ThreadsUnavailable);

        var load = vm.LoadAsync(TestContext.Current.CancellationToken);
        source.FailThreads(new HttpRequestException("threads down"));
        await load;

        Assert.True(vm.ThreadsUnavailable);

        // LoadAsync runs once per dialog, so the threads never arrive later. Error is transient —
        // the next operation clears it — but this degraded state must outlive it: otherwise the
        // first keypress publishes a diff, the view's header precedence overwrites the error, and
        // the reviewer sees a clean file with no markers on a PR that has review comments.
        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.True(vm.ThreadsUnavailable);
        Assert.NotNull(vm.CurrentDiff);
        Assert.Empty(vm.Threads);
    }

    [Fact]
    public async Task A_Pr_With_No_Threads_Is_Not_Marked_Threads_Unavailable()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // "No threads" and "threads unknown" are different facts; conflating them is the bug.
        Assert.Empty(vm.Threads);
        Assert.False(vm.ThreadsUnavailable);
    }

    /// <summary>A loaded PR with one file on screen, ready to comment on.</summary>
    private static async Task<PrDiffViewModel> LoadedForComment(FakeDiffSource source)
    {
        source.Changes = [new FileChange("/a.cs", FileChangeKind.Edit)];
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task A_Failed_Threads_Refresh_After_A_Comment_Marks_Threads_Unavailable()
    {
        // The load direction is not the only one. A PR that genuinely has no comments loads clean,
        // the reviewer posts a line comment, the post succeeds and only the refresh fails: Threads
        // is empty and stale, so the title would say "0 unresolved" while a comment the reviewer
        // just wrote exists server-side. Same approve-blind failure, reached by mutation.
        var source = new FakeDiffSource { Threads = [] };
        var vm = await LoadedForComment(source);
        Assert.False(vm.ThreadsUnavailable);
        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);

        source.ThreadsError = new HttpRequestException("threads down");
        await vm.AddCommentAtLineAsync(addedIndex, "nice", TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastComment); // the comment really was posted
        Assert.True(vm.ThreadsUnavailable);
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task A_Successful_Threads_Refresh_Clears_Threads_Unavailable()
    {
        // Persistent is not permanent. If the load lost the threads but a later refresh gets them,
        // the markers are back on screen — leaving the indicator up would be a false alarm, and a
        // warning that cries wolf trains the reviewer to ignore the one that matters.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(7, PrThreadStatus.Active, [], "/a.cs", 2, null)],
            ThreadsError = new HttpRequestException("threads down"),
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.ThreadsUnavailable);

        source.ThreadsError = null; // the outage passes
        await vm.ReplyToThreadAsync(7, "thanks", TestContext.Current.CancellationToken);

        Assert.False(vm.ThreadsUnavailable);
        Assert.Single(vm.Threads);
    }

    [Fact]
    public async Task A_Failed_Comment_Post_Does_Not_Mark_Threads_Unavailable()
    {
        // The mutation failing is not a threads problem: the refresh never ran, so nothing was
        // learned about the threads. Reporting the comments as unknown here would be a false alarm.
        var source = new FakeDiffSource { Threads = [] };
        var vm = await LoadedForComment(source);
        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);

        source.MutationError = new HttpRequestException("comment rejected");
        await vm.AddCommentAtLineAsync(addedIndex, "nice", TestContext.Current.CancellationToken);

        Assert.False(vm.ThreadsUnavailable);
        Assert.NotNull(vm.Error); // the rejection still surfaces, as a transient error
    }

    [Fact]
    public async Task A_Failed_Mutation_Does_Not_Clear_An_Existing_Threads_Unavailable()
    {
        // The mirror: a mutation that fails before its refresh has learned nothing either way, so
        // it must not quietly retract a degradation the load established.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            ThreadsError = new HttpRequestException("threads down"),
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.ThreadsUnavailable);

        source.MutationError = new HttpRequestException("reply rejected");
        await vm.ReplyToThreadAsync(7, "thanks", TestContext.Current.CancellationToken);

        Assert.True(vm.ThreadsUnavailable);
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
    public async Task Concurrent_Selects_Never_Leave_CurrentDiff_And_Path_Describing_Different_Files()
    {
        var source = new GatedBlobSource
        {
            Changes =
            [
                new FileChange("/a.cs", FileChangeKind.Edit),
                new FileChange("/b.cs", FileChangeKind.Edit),
                new FileChange("/c.cs", FileChangeKind.Edit),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        // Distinct addition counts, so a pair describing two different files is detectable.
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\nb1\n";
        source.Blobs[("/c.cs", "base")] = "1\n";
        source.Blobs[("/c.cs", "src")] = "1\nc1\nc2\nc3\n";
        var vm = new PrDiffViewModel(source, Pr());

        source.Release();
        await vm.LoadAsync(TestContext.Current.CancellationToken); // warms /a.cs only
        source.Rearm();

        // Two rapid `j` presses: both fetches go in flight, then both continuations resume at once.
        var toB = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        var toC = vm.SelectFileAsync(2, TestContext.Current.CancellationToken);
        source.Release();
        await Task.WhenAll(toB, toC);

        // Which select lands last is deliberately not asserted: last-writer-wins is pre-existing
        // CurrentDiff behaviour. The contract is only that the two agree with *each other*, so the
        // view never renders one file's path above another file's stats.
        Assert.NotNull(vm.CurrentDiff);
        Assert.Contains(vm.CurrentDiffPath, (string?[])["/b.cs", "/c.cs"]);
        Assert.Equal(vm.CurrentDiffPath == "/b.cs" ? 1 : 3, vm.CurrentDiff!.Additions);
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
    public async Task Thread_Lookup_Anchors_To_The_Displayed_Diffs_File_Not_The_Pending_Selection()
    {
        var source = new GatedBlobSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "on a", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(1, "Sam", "on b", false)], "/b.cs", 2, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());

        source.Release();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/a.cs", vm.CurrentDiffPath);
        source.Rearm(); // /b.cs will be slow to arrive

        // The reviewer starts navigating to /b.cs, but /a.cs is still the diff on screen and still
        // the diff their cursor is in.
        var select = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal("/b.cs", vm.SelectedFile?.Path);
        Assert.Equal("/a.cs", vm.CurrentDiffPath);

        var addedLine = vm.CurrentDiff!.Lines.First(l => l.Kind == DiffLineKind.Added);
        var found = vm.ThreadsForDiffLine(addedLine);

        // These threads open the thread overlay, whose reply/resolve/reactivate mutate them: a
        // lookup anchored to the pending selection would let the reviewer reply to a comment on a
        // file they are not looking at.
        var thread = Assert.Single(found);
        Assert.Equal(1, thread.Id);
        Assert.Equal("/a.cs", thread.FilePath);

        source.Release();
        await select;
    }

    [Fact]
    public async Task Comment_Anchors_To_The_Displayed_Diffs_File_Not_The_Pending_Selection()
    {
        var source = new GatedBlobSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());

        source.Release();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/a.cs", vm.CurrentDiffPath);
        source.Rearm(); // /b.cs will be slow to arrive

        // The reviewer starts navigating to /b.cs: SelectedFile moves immediately, but /a.cs is
        // still the diff on screen and still the diff their cursor is sitting in.
        var select = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal("/b.cs", vm.SelectedFile?.Path);
        Assert.Equal("/a.cs", vm.CurrentDiffPath);

        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);
        await vm.AddCommentAtLineAsync(addedIndex, "nice", TestContext.Current.CancellationToken);

        // The line number came from /a.cs's diff, so the comment must post to /a.cs. Posting it to
        // the pending selection writes a colleague's PR at another file's line number.
        Assert.Equal("/a.cs", source.LastComment?.path);
        Assert.Equal(2, source.LastComment?.line);
        Assert.True(source.LastComment?.right);

        source.Release();
        await select;
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
    public async Task PrefetchAllDiffsAsync_Stops_Claiming_New_Files_When_Cancelled()
    {
        var source = new FakeDiffSource { Changes = EightAddedFiles() };
        // Only the first four files have blobs. Any file claimed after the cancel would still
        // "succeed" with empty text and register stats, so a null StatsFor proves it was never
        // claimed rather than merely never returned.
        for (var i = 0; i < 4; i++)
        {
            source.Blobs[($"/f{i}.cs", "src")] = "a\n";
        }
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        // Each worker raises StatsChanged before looping, so the first completed file cancels
        // before any worker can claim a fifth.
        vm.StatsChanged += () => cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.PrefetchAllDiffsAsync(cts.Token));

        // With bounded concurrency, cancelling stops the next *claim*, not the in-flight wave:
        // files already being fetched (up to the worker count) still land. Nothing beyond may.
        Assert.NotNull(vm.StatsFor("/f0.cs"));
        for (var i = 4; i < 8; i++)
        {
            Assert.Null(vm.StatsFor($"/f{i}.cs"));
        }
    }

    [Fact]
    public async Task Prefetch_Fetches_Four_Files_Concurrently()
    {
        var source = new OrderedGateSource { Changes = EightAddedFiles() };
        var vm = new PrDiffViewModel(source, Pr());
        source.Release("/f0.cs"); // let the initial load finish and cache the first file
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // The load already requested /f0.cs (request 0) and cached it, so the prefetch's own
        // requests start at index 1.
        Assert.Single(source.Requested);

        var prefetch = vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);

        // A sequential prefetch keeps exactly one blob in flight, so a 40-file PR warms one serial
        // round-trip at a time. Four workers cap the concurrency without going unbounded.
        Assert.True(
            await source.WaitForRequestsAsync(5, TimeSpan.FromSeconds(5)),
            "four prefetch blob requests must be in flight at once");
        Assert.False(
            await source.WaitForRequestsAsync(6, TimeSpan.FromMilliseconds(200)),
            "and no more than four: the prefetch is bounded");

        for (var i = 1; i < 8; i++)
        {
            source.Release($"/f{i}.cs");
        }
        await prefetch;
    }

    [Fact]
    public async Task Prefetch_Claims_The_File_Nearest_The_Selection()
    {
        var source = new OrderedGateSource { Changes = EightAddedFiles() };
        var vm = new PrDiffViewModel(source, Pr());
        source.Release("/f0.cs");
        await vm.LoadAsync(TestContext.Current.CancellationToken); // caches /f0.cs, selection = 0

        Assert.Single(source.Requested); // the load's /f0.cs

        var prefetch = vm.PrefetchAllDiffsAsync(TestContext.Current.CancellationToken);
        Assert.True(await source.WaitForRequestsAsync(5, TimeSpan.FromSeconds(5)));
        // The workers start beside the selection (0). /f0.cs is already cached, so the four in
        // flight are 1-4 — the reviewer's next rows — never the far end of the PR.
        Assert.Equal(
            ["/f1.cs", "/f2.cs", "/f3.cs", "/f4.cs"],
            [.. source.Requested.Skip(1).Order(StringComparer.Ordinal)]);

        // The reviewer jumps to the far end while the prefetch is still saturated.
        var select = vm.SelectFileAsync(7, TestContext.Current.CancellationToken);
        Assert.True(await source.WaitForRequestsAsync(6, TimeSpan.FromSeconds(5)));
        Assert.Equal("/f7.cs", source.Requested[5]);
        source.Release("/f7.cs");
        await select;

        // Freeing one worker: it must pick up beside where the reviewer now is (6), not resume
        // blind index order (5). /f7.cs is already cached, so /f6.cs is the nearest un-fetched.
        source.Release("/f1.cs");
        Assert.True(
            await source.WaitForRequestsAsync(7, TimeSpan.FromSeconds(5)),
            "the freed worker must claim another file");
        Assert.Equal("/f6.cs", source.Requested[6]);

        for (var i = 1; i < 8; i++)
        {
            source.Release($"/f{i}.cs");
        }
        await prefetch;
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

    // ---- RENDER-2 (VM half): UnresolvedFilePaths, computed once per Threads write ----

    [Fact]
    public async Task UnresolvedFilePaths_Lists_Only_Files_With_An_Active_NonSystem_Thread()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "resolved", false)], "/b.cs", 3, null),
                new PrThread(3, PrThreadStatus.Active, [new PrComment(1, "System", "system", true)], "/b.cs", 4, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        source.Blobs[("/b.cs", "base")] = "y\n";
        source.Blobs[("/b.cs", "src")] = "y\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // Only /a.cs carries an unresolved (Active, non-system) thread; /b.cs's are resolved/system.
        Assert.Equal(["/a.cs"], vm.UnresolvedFilePaths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnresolvedFilePaths_Is_A_Stable_Snapshot_Recomputed_On_Each_Threads_Write()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // One O(threads) pass per Threads write, not one per read: repeated reads return the
        // identical snapshot instance (the render probes it per file).
        var first = vm.UnresolvedFilePaths;
        Assert.Same(first, vm.UnresolvedFilePaths);
        Assert.Equal(["/a.cs"], first.Order(StringComparer.Ordinal));

        // A threads refresh (resolve) rewrites Threads, so the snapshot is recomputed.
        source.Threads = [new PrThread(1, PrThreadStatus.Fixed, [new PrComment(1, "Sam", "note", false)], "/a.cs", 2, null)];
        await vm.ResolveThreadAsync(1, TestContext.Current.CancellationToken);

        Assert.NotSame(first, vm.UnresolvedFilePaths);
        Assert.Empty(vm.UnresolvedFilePaths);
    }
}
