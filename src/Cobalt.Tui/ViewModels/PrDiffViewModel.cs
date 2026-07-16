using System.Collections.Concurrent;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Text;

namespace Cobalt.Tui.ViewModels;

public interface IPrDiffSource
{
    Task<PrIteration?> GetLatestIterationAsync(string project, string repositoryId, int prId, CancellationToken ct);
    Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repositoryId, int prId, int iterationId, CancellationToken ct);
    Task<string> GetFileContentAsync(string project, string repositoryId, string path, string commit, CancellationToken ct);
    Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repositoryId, int prId, CancellationToken ct);
    Task AddLineCommentAsync(string project, string repositoryId, int prId, string path, int line, bool rightSide, string text, CancellationToken ct);
    Task ReplyToThreadAsync(string project, string repositoryId, int prId, int threadId, string text, CancellationToken ct);
    Task SetThreadStatusAsync(string project, string repositoryId, int prId, int threadId, PrThreadStatus status, CancellationToken ct);
    Task VoteAsync(string project, string repositoryId, int prId, PrVote vote, CancellationToken ct);
}

/// <summary>
/// Diff review for a PR's latest iteration: changed-file list, per-file unified
/// diff (fetched lazily and cached), thread markers mapped to lines, and adding
/// line comments with the correct left/right anchor (SPEC §3, M5). UI-free.
/// </summary>
public sealed class PrDiffViewModel(IPrDiffSource source, PullRequest pr)
{
    // Concurrent: the background stats prefetch writes on threadpool continuations while the
    // UI thread reads it (StatsFor / TotalAdditions) during render, and user navigation is a
    // second writer — a plain Dictionary would throw mid-enumeration on a multi-file PR.
    private readonly ConcurrentDictionary<string, FileDiff> _diffCache = new(StringComparer.Ordinal);

    // Fetches currently in flight, so the background prefetch and user navigation landing on the
    // same uncached file share one fetch rather than issuing a duplicate pair of blob requests.
    // Lazy: GetOrAdd may run its factory more than once under contention, and only the published
    // Lazy's task must ever be started.
    private readonly ConcurrentDictionary<string, Lazy<Task<FileDiff>>> _inflight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _viewed = new(StringComparer.Ordinal);

    /// <summary>
    /// How many <em>files</em> the background prefetch warms at once. Each file fetches its base
    /// and source blobs concurrently, so this is 4 files = up to 8 concurrent blob requests, plus
    /// up to 2 more from user navigation. That ceiling is well inside what the org tolerates, and
    /// the Polly retry is Retry-After aware if a burst ever does draw a 429.
    /// Deliberately not configurable.
    /// </summary>
    private const int PrefetchWorkers = 4;

    private readonly object _prefetchLock = new();
    private readonly HashSet<int> _prefetchClaimed = [];
    private PrIteration? _iteration;
    private int _selectedFileIndex;

    public int PrId => pr.PullRequestId;
    public PullRequest PullRequest => pr;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<FileChange> Files { get; private set; } = [];
    public IReadOnlyList<PrThread> Threads { get; private set; } = [];

    /// <summary>
    /// The set of file paths carrying at least one unresolved (Active, non-system) thread.
    /// Computed once per <see cref="Threads"/> write (in <see cref="HarvestThreadsAsync"/>), so
    /// the render can decide a file's "has unresolved comments" annotation by an O(1) lookup
    /// instead of scanning every thread per file. The reference is stable between writes.
    /// </summary>
    public IReadOnlySet<string> UnresolvedFilePaths { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when the review threads could not be loaded, so comment markers and thread
    /// navigation are unavailable for this dialog session (LoadAsync runs once).</summary>
    public bool ThreadsUnavailable { get; private set; }

    /// <summary>
    /// The displayed diff and the file it came from, held as one object so the pair is published by
    /// a single (atomic) reference write. Two overlapping selects resuming on threadpool threads
    /// could otherwise interleave between two separate field writes and leave one file's diff under
    /// another file's path. Which select lands last is not defined here — only that the two agree.
    /// </summary>
    private sealed record DiffState(FileDiff Diff, string Path);

    private DiffState? _current;

    public FileDiff? CurrentDiff => _current?.Diff;

    /// <summary>The file path <see cref="CurrentDiff"/> belongs to; null when there is no diff.</summary>
    public string? CurrentDiffPath => _current?.Path;

    public event Action? Changed;

    /// <summary>
    /// Raised when only per-file diff stats/totals change (the background prefetch), not the
    /// displayed diff content. Lets the view refresh the title totals and file-row stats without
    /// re-tokenizing the open file — see <see cref="PrefetchAllDiffsAsync"/>.
    /// </summary>
    public event Action? StatsChanged;

    /// <summary>
    /// Raised when only the busy/error chrome changes at the <em>start</em> of a mutation, not the
    /// diff content or threads. Lets the dialog refresh the busy indicator and error header without
    /// re-tokenizing the open file on every keystroke that starts an operation (mirror of
    /// <see cref="StatsChanged"/>). The trailing completion still raises <see cref="Changed"/>,
    /// because the threads refresh that lands with it is a real content change.
    /// </summary>
    public event Action? BusyChanged;

    /// <summary>
    /// Raised once per load, the instant <see cref="Files"/> is assigned — before the first file's
    /// diff is published. Lets a consumer start its background prefetch off the changed-file list
    /// without waiting for the whole load (threads + first diff) to settle (ASYNC-3).
    /// </summary>
    public event Action? FilesLoaded;

    public FileChange? SelectedFile =>
        Files.Count == 0 ? null : Files[Math.Clamp(_selectedFileIndex, 0, Files.Count - 1)];

    public int UnresolvedThreadCount => Threads.Count(IsUnresolved);

    public bool OnlyUnresolvedFiles { get; set; }

    /// <summary>
    /// A filtered projection of <see cref="Files"/> for display only. Never reorders or mutates
    /// <see cref="Files"/> itself, so existing index-based navigation (SelectFileAsync) stays valid.
    /// </summary>
    public IReadOnlyList<FileChange> FilteredFiles =>
        OnlyUnresolvedFiles
            ? [.. Files.Where(f => UnresolvedFilePaths.Contains(f.Path))]
            : Files;

    private static bool IsUnresolved(PrThread thread) =>
        thread.Status == PrThreadStatus.Active && !thread.IsSystemOnly;

    private static IReadOnlySet<string> ComputeUnresolvedFilePaths(IReadOnlyList<PrThread> threads)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var thread in threads)
        {
            if (IsUnresolved(thread) && thread.FilePath is { } path)
            {
                set.Add(path);
            }
        }
        return set;
    }

    public void MarkViewed(string path) => _viewed.Add(path);

    public void MarkUnviewed(string path) => _viewed.Remove(path);

    public bool IsViewed(string path) => _viewed.Contains(path);

    /// <summary>Additions/deletions for a file whose diff has already been computed; null otherwise.</summary>
    public (int Additions, int Deletions)? StatsFor(string path) =>
        _diffCache.TryGetValue(path, out var diff) ? (diff.Additions, diff.Deletions) : null;

    public int TotalAdditions => _diffCache.Values.Sum(d => d.Additions);
    public int TotalDeletions => _diffCache.Values.Sum(d => d.Deletions);

    /// <summary>
    /// Computes every file's diff into the cache (so <see cref="StatsFor"/> and the totals fill
    /// in), raising <see cref="StatsChanged"/> after each. Files are taken nearest-first from the
    /// current selection by <see cref="PrefetchWorkers"/> workers, so the rows the reviewer is
    /// about to reach warm first and a large PR does not warm one serial round-trip at a time.
    /// Single-flight means a file the reviewer opens mid-prefetch shares the in-flight fetch.
    /// This never changes the selected file or <see cref="CurrentDiff"/>, so it deliberately does
    /// not raise <see cref="Changed"/>: the view refreshes only the title totals and file-row
    /// stats, not the (unchanged) displayed diff — otherwise an N-file PR re-tokenizes the open
    /// file N times. Cancelling stops the next claim, not the in-flight wave.
    /// </summary>
    public async Task PrefetchAllDiffsAsync(CancellationToken ct)
    {
        lock (_prefetchLock)
        {
            _prefetchClaimed.Clear();
        }

        var workers = Math.Min(PrefetchWorkers, Files.Count);
        if (workers == 0)
        {
            return;
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => PrefetchWorkerAsync(ct))).ConfigureAwait(false);
    }

    private async Task PrefetchWorkerAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ClaimNearestUnfetched() is not { } index)
            {
                return; // every file is claimed
            }

            try
            {
                await ComputeDiffForFileAsync(Files[index], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
            {
                throw; // genuine user/dialog cancel (carries our token) stops the whole prefetch
            }
            catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
            {
                // One file's blob fetch failed (e.g. a 404 on a rename/edge path). Skip its
                // stats and keep prefetching the rest — background stats are best-effort.
            }
            StatsChanged?.Invoke();
        }
    }

    /// <summary>Claims the un-fetched file nearest the current selection; null when none remain.</summary>
    private int? ClaimNearestUnfetched()
    {
        // The lock covers the claim only and is never held across the fetch. The selection is read
        // here rather than captured once, so a worker freed mid-prefetch picks up beside wherever
        // the reviewer has since navigated; racing with SelectFileAsync's write only costs one
        // file's priority, so that writer stays lock-free.
        lock (_prefetchLock)
        {
            if (NearestUnclaimed(Files.Count, _prefetchClaimed, _selectedFileIndex) is not { } index)
            {
                return null;
            }
            _prefetchClaimed.Add(index);
            return index;
        }
    }

    /// <summary>
    /// The unclaimed index nearest <paramref name="selectedIndex"/>, or null when all are claimed.
    /// Ties go to the lower index. Pure.
    /// </summary>
    internal static int? NearestUnclaimed(int fileCount, IReadOnlySet<int> claimed, int selectedIndex)
    {
        int? nearest = null;
        var nearestDistance = int.MaxValue;
        for (var i = 0; i < fileCount; i++)
        {
            if (claimed.Contains(i))
            {
                continue;
            }
            var distance = Math.Abs(i - selectedIndex);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = i;
            }
        }
        return nearest;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        Task<FileDiff?>? diffTask = null;
        try
        {
            _iteration = await source.GetLatestIterationAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct).ConfigureAwait(false);
            if (_iteration is null)
            {
                Error = "this pull request has no iterations to diff";
                Files = [];
                return;
            }

            Files = await source.GetIterationChangesAsync(
                pr.ProjectName, pr.RepositoryId, pr.PullRequestId, _iteration.Id, ct).ConfigureAwait(false);
            // Signal the changed-file list is ready so a consumer can start prefetch now, before
            // the threads fetch and first diff below complete (ASYNC-3).
            FilesLoaded?.Invoke();

            // Threads need only the PR id and the first file's blobs need only the iteration, so
            // both round-trips start here and are harvested below: an open costs ~3 round-trips
            // rather than 5. They start after the files fetch (not at the top of the method)
            // because the blobs cannot start any earlier than this anyway, and the early return
            // above would otherwise abandon an in-flight task.
            var threadsTask = source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct);

            _selectedFileIndex = 0;
            var file = SelectedFile;
            diffTask = file is null ? null : ComputeDiffForFileAsync(file, ct);

            // Threads are harvested before the diff is published, and both land before the single
            // paint in the finally. This order is load-bearing, not stylistic:
            //  - a threads failure must leave CurrentDiff null, because the view renders Error only
            //    while CurrentDiff is null and otherwise overwrites the error header with the
            //    file's stats — a swallowed failure shows a clean diff with no markers on a PR
            //    that has review comments;
            //  - a diff failure must still leave Threads populated: LoadAsync runs once per dialog,
            //    so dropping them here costs the whole session its comment markers.
            // Awaiting does not serialise the two: both requests are already in flight.
            await HarvestThreadsAsync(threadsTask, ct).ConfigureAwait(false);

            if (file is not null && diffTask is not null)
            {
                var diff = await diffTask.ConfigureAwait(false);
                _current = diff is null ? null : new DiffState(diff, file.Path);
            }
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            // The blobs are started before the threads are harvested, so a threads failure leaves
            // this fetch running unawaited. Observing it on every path (rather than in each catch)
            // keeps its fault off the crash log; on the happy path it is already awaited and this
            // is a no-op. The threads fetch never needs this: it is always the first await.
            ObserveFault(diffTask);
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Harvests a review-threads fetch and records whether the threads are available, then rethrows
    /// a failure for the caller's filters to turn into <see cref="Error"/>.
    ///
    /// <para>The state tracks the <em>most recent</em> fetch, not the first (ADR 0013). Success
    /// clears it, so an outage that has passed stops warning — a stale warning while markers are on
    /// screen is a false alarm, and one that cries wolf trains the reviewer to ignore the one that
    /// matters. Failure sets it, so a refresh that fails after a clean load still degrades: an empty
    /// <see cref="Threads"/> would otherwise read as "no comments" on a PR that has them.</para>
    ///
    /// <para>Every thread fetch goes through here so none can forget the flag, and it takes the
    /// fetch already in flight rather than starting one, so <see cref="LoadAsync"/> keeps overlapping
    /// it with the blobs. A genuine user cancel is deliberately not caught — the dialog is closing,
    /// not degraded — and a caller whose mutation fails before calling this leaves the state alone,
    /// because a rejected post says nothing about the threads.</para>
    /// </summary>
    private async Task HarvestThreadsAsync(Task<IReadOnlyList<PrThread>> fetch, CancellationToken ct)
    {
        try
        {
            Threads = await fetch.ConfigureAwait(false);
            // Recompute the unresolved-file set once here, on the single Threads write, rather
            // than scanning every thread per file on each render (RENDER-2).
            UnresolvedFilePaths = ComputeUnresolvedFilePaths(Threads);
            ThreadsUnavailable = false;
        }
        catch (Exception ex) when (
            AdoExceptions.IsExpected(ex) || (ex is OperationCanceledException oce && AdoExceptions.IsTimeout(oce, ct)))
        {
            ThreadsUnavailable = true;
            throw; // the caller's filters own the Error message
        }
    }

    /// <summary>
    /// Observes a fetch that was started but left unawaited because something before its await
    /// failed. Without this its fault would resurface as a phantom crash-log entry via the
    /// <see cref="TaskScheduler.UnobservedTaskException"/> hook, with no message bar (ADR 0013) —
    /// the error the user actually needs is the one already in <see cref="Error"/>. Harmless on an
    /// already-awaited task, so callers can invoke it unconditionally on every exit path.
    /// </summary>
    private static void ObserveFault(Task? task) =>
        _ = task?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task SelectFileAsync(int index, CancellationToken ct)
    {
        _selectedFileIndex = Math.Clamp(index, 0, Math.Max(0, Files.Count - 1));
        await ComputeCurrentDiffAsync(ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Threads anchored to a diff line: right side for added/context, left side for removed.
    /// Anchored to the displayed diff rather than <see cref="SelectedFile"/>, which moves the
    /// instant the reviewer navigates while this line still belongs to the diff on screen. These
    /// threads open the thread overlay, whose reply/resolve/reactivate mutate them, so anchoring
    /// to a pending selection would land a reply on a thread in a file nobody is looking at.
    /// </summary>
    public IReadOnlyList<PrThread> ThreadsForDiffLine(DiffLine line)
    {
        // Read once: the line and the file it is matched against must come from one DiffState.
        var current = _current;
        if (current is null)
        {
            return [];
        }
        return
        [
            .. Threads.Where(t =>
                string.Equals(t.FilePath, current.Path, StringComparison.Ordinal) &&
                (line.Kind == DiffLineKind.Removed
                    ? line.OldLineNumber is { } l && t.LeftLine == l
                    : line.NewLineNumber is { } r && t.RightLine == r)),
        ];
    }

    /// <summary>
    /// Line numbers carrying at least one thread on each side, for one file. Pure; built once per
    /// render so the view can test a line for markers by lookup instead of scanning
    /// <see cref="Threads"/> per line. Sides are anchored as in <see cref="ThreadsForDiffLine"/>.
    /// </summary>
    public (IReadOnlySet<int> Left, IReadOnlySet<int> Right) CommentedLinesFor(string path)
    {
        var left = new HashSet<int>();
        var right = new HashSet<int>();
        foreach (var thread in Threads)
        {
            if (!string.Equals(thread.FilePath, path, StringComparison.Ordinal))
            {
                continue;
            }
            if (thread.LeftLine is { } l)
            {
                left.Add(l);
            }
            if (thread.RightLine is { } r)
            {
                right.Add(r);
            }
        }
        return (left, right);
    }

    /// <summary>The current threads matching the given ids, in id order, skipping any not present.</summary>
    public IReadOnlyList<PrThread> ThreadsByIds(IReadOnlyList<int> ids) =>
        [.. ids.Select(id => Threads.FirstOrDefault(t => t.Id == id)).OfType<PrThread>()];

    public async Task AddCommentAtLineAsync(int diffLineIndex, string text, CancellationToken ct)
    {
        // Read the displayed state once, and take both the line and the file it posts to from that
        // one DiffState. SelectedFile is NOT the file on screen: it moves the instant the reviewer
        // navigates, while the new diff is still being fetched — anchoring to it would post this
        // line number against a different file.
        var current = _current;
        if (current is null || diffLineIndex < 0 || diffLineIndex >= current.Diff.Lines.Count)
        {
            return;
        }

        var line = current.Diff.Lines[diffLineIndex];
        // Comment on the right (new) side for context/added lines, left for deletions.
        var rightSide = line.Kind != DiffLineKind.Removed;
        var lineNumber = rightSide ? line.NewLineNumber : line.OldLineNumber;
        if (lineNumber is null)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        // Chrome-only signal: the diff content and threads are unchanged at the busy flip, so this
        // must not re-tokenize the open file. The trailing Changed (below) covers the real refresh.
        BusyChanged?.Invoke();
        try
        {
            await source.AddLineCommentAsync(
                pr.ProjectName, pr.RepositoryId, pr.PullRequestId, current.Path, lineNumber.Value, rightSide, text, ct)
                .ConfigureAwait(false);
            // Separate awaits, so a failure is attributed to the right thing: the post throwing
            // skips the harvest entirely and leaves the threads state untouched, while only the
            // harvest can mark the threads unavailable.
            await HarvestThreadsAsync(
                source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public Task ReplyToThreadAsync(int threadId, string text, CancellationToken ct) =>
        RunThreadMutationAsync(() =>
            source.ReplyToThreadAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, threadId, text, ct), ct);

    public Task ResolveThreadAsync(int threadId, CancellationToken ct) =>
        RunThreadMutationAsync(() =>
            source.SetThreadStatusAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, threadId, PrThreadStatus.Fixed, ct), ct);

    public Task ReactivateThreadAsync(int threadId, CancellationToken ct) =>
        RunThreadMutationAsync(() =>
            source.SetThreadStatusAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, threadId, PrThreadStatus.Active, ct), ct);

    public async Task VoteAsync(PrVote vote, CancellationToken ct)
    {
        IsBusy = true;
        Error = null;
        // Chrome-only signal: the diff content and threads are unchanged at the busy flip, so this
        // must not re-tokenize the open file. The trailing Changed (below) covers the real refresh.
        BusyChanged?.Invoke();
        try
        {
            await source.VoteAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, vote, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    private async Task RunThreadMutationAsync(Func<Task> mutate, CancellationToken ct)
    {
        IsBusy = true;
        Error = null;
        // Chrome-only signal: the diff content and threads are unchanged at the busy flip, so this
        // must not re-tokenize the open file. The trailing Changed (below) covers the real refresh.
        BusyChanged?.Invoke();
        try
        {
            await mutate().ConfigureAwait(false);
            // Separate awaits: a mutation that fails never reaches the harvest, so it cannot mark
            // the threads unavailable or retract a degradation the load established.
            await HarvestThreadsAsync(
                source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    private async Task ComputeCurrentDiffAsync(CancellationToken ct)
    {
        var file = SelectedFile;
        var diff = file is null ? null : await ComputeDiffForFileAsync(file, ct).ConfigureAwait(false);
        // One reference write publishes both halves: a reader sees this whole pair or none of it.
        _current = diff is null || file is null ? null : new DiffState(diff, file.Path);
    }

    /// <summary>Computes (or returns the cached) diff for one file. Does not touch <see cref="CurrentDiff"/>.</summary>
    private async Task<FileDiff?> ComputeDiffForFileAsync(FileChange file, CancellationToken ct)
    {
        if (_iteration is not { } iteration)
        {
            return null;
        }
        if (_diffCache.TryGetValue(file.Path, out var cached))
        {
            return cached;
        }

        // The shared task carries whichever caller started it — its token included. That is safe
        // because the prefetch and the selection both pass the dialog's token, so no caller can
        // cancel a fetch another caller is awaiting without cancelling its own too.
        var inflight = _inflight.GetOrAdd(
            file.Path,
            _ => new Lazy<Task<FileDiff>>(() => FetchDiffAsync(file, iteration, ct)));
        try
        {
            var diff = await inflight.Value.ConfigureAwait(false);
            // Published before the eviction below, so a caller that misses the result cache still
            // finds the in-flight entry rather than starting a second fetch.
            _diffCache[file.Path] = diff;
            return diff;
        }
        finally
        {
            // Evicted on success and on failure alike: a file whose fetch failed must be
            // retryable by a later select, never stuck behind a permanently faulted task.
            // Removed by key *and value*, so a late awaiter cannot evict a newer entry that
            // another caller registered after this fetch finished — that would send the newer
            // caller's joiners on duplicate round-trips, defeating the point of sharing.
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<FileDiff>>>(file.Path, inflight));
        }
    }

    private async Task<FileDiff> FetchDiffAsync(FileChange file, PrIteration iteration, CancellationToken ct)
    {
        // Added files have no base version; deleted files have no source version.
        // Renamed/moved files have their base blob at the old path.
        // The two blobs are independent, so both requests are started before either is awaited:
        // a cache miss costs one round-trip rather than two.
        var basePath = file.OriginalPath ?? file.Path;
        var baseTask = file.ChangeType == FileChangeKind.Add
            ? Task.FromResult("")
            : source.GetFileContentAsync(pr.ProjectName, pr.RepositoryId, basePath, iteration.BaseCommitId ?? "", ct);
        var sourceTask = file.ChangeType == FileChangeKind.Delete
            ? Task.FromResult("")
            : source.GetFileContentAsync(pr.ProjectName, pr.RepositoryId, file.Path, iteration.SourceCommitId ?? "", ct);

        // WhenAll (rather than two sequential awaits) so a failure of the first blob still observes
        // the second's exception; it rethrows the first fault, keeping the ADR 0013 filters intact.
        var texts = await Task.WhenAll(baseTask, sourceTask).ConfigureAwait(false);

        return DiffService.Unified(texts[0], texts[1]);
    }
}
