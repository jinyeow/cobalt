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
    private PrIteration? _iteration;
    private int _selectedFileIndex;

    public int PrId => pr.PullRequestId;
    public PullRequest PullRequest => pr;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<FileChange> Files { get; private set; } = [];
    public IReadOnlyList<PrThread> Threads { get; private set; } = [];
    public FileDiff? CurrentDiff { get; private set; }

    /// <summary>The file path <see cref="CurrentDiff"/> belongs to; null when there is no diff.</summary>
    public string? CurrentDiffPath { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Raised when only per-file diff stats/totals change (the background prefetch), not the
    /// displayed diff content. Lets the view refresh the title totals and file-row stats without
    /// re-tokenizing the open file — see <see cref="PrefetchAllDiffsAsync"/>.
    /// </summary>
    public event Action? StatsChanged;

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
            ? [.. Files.Where(f => Threads.Any(t => IsUnresolved(t) && string.Equals(t.FilePath, f.Path, StringComparison.Ordinal)))]
            : Files;

    private static bool IsUnresolved(PrThread thread) =>
        thread.Status == PrThreadStatus.Active && !thread.IsSystemOnly;

    public void MarkViewed(string path) => _viewed.Add(path);

    public void MarkUnviewed(string path) => _viewed.Remove(path);

    public bool IsViewed(string path) => _viewed.Contains(path);

    /// <summary>Additions/deletions for a file whose diff has already been computed; null otherwise.</summary>
    public (int Additions, int Deletions)? StatsFor(string path) =>
        _diffCache.TryGetValue(path, out var diff) ? (diff.Additions, diff.Deletions) : null;

    public int TotalAdditions => _diffCache.Values.Sum(d => d.Additions);
    public int TotalDeletions => _diffCache.Values.Sum(d => d.Deletions);

    /// <summary>
    /// Computes every file's diff sequentially into the cache (so <see cref="StatsFor"/> and the
    /// totals fill in), raising <see cref="StatsChanged"/> after each. This never changes the
    /// selected file or <see cref="CurrentDiff"/>, so it deliberately does not raise
    /// <see cref="Changed"/>: the view refreshes only the title totals and file-row stats, not the
    /// (unchanged) displayed diff — otherwise an N-file PR re-tokenizes the open file N times.
    /// </summary>
    public async Task PrefetchAllDiffsAsync(CancellationToken ct)
    {
        foreach (var file in Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ComputeDiffForFileAsync(file, ct).ConfigureAwait(false);
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

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        Task<IReadOnlyList<PrThread>>? threadsTask = null;
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

            // Threads depend only on the PR id, so this round-trip overlaps the first diff's blobs
            // instead of preceding them. It starts here rather than at the top of the method
            // because the blobs cannot start any earlier than this anyway (they need the
            // iteration's commit ids), and the early return above would otherwise abandon it.
            threadsTask = source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct);

            _selectedFileIndex = 0;
            if (Files.Count > 0)
            {
                await ComputeCurrentDiffAsync(ct).ConfigureAwait(false);
                // First paint: the diff is ready, so show it now rather than behind the threads
                // fetch. The finally's Changed then fills in the thread markers.
                IsLoading = false;
                Changed?.Invoke();
            }

            Threads = await threadsTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            ObserveFault(threadsTask);
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
            ObserveFault(threadsTask);
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Observes a fetch that was started but left unawaited because something before its await
    /// failed. Without this its fault would resurface as a phantom crash-log entry via the
    /// <see cref="TaskScheduler.UnobservedTaskException"/> hook, with no message bar (ADR 0013) —
    /// the error the user actually needs is the one already in <see cref="Error"/>.
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

    /// <summary>Threads anchored to a diff line: right side for added/context, left side for removed.</summary>
    public IReadOnlyList<PrThread> ThreadsForDiffLine(DiffLine line)
    {
        var path = SelectedFile?.Path;
        if (path is null)
        {
            return [];
        }
        return
        [
            .. Threads.Where(t =>
                string.Equals(t.FilePath, path, StringComparison.Ordinal) &&
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
        if (CurrentDiff is null || SelectedFile is null ||
            diffLineIndex < 0 || diffLineIndex >= CurrentDiff.Lines.Count)
        {
            return;
        }

        var line = CurrentDiff.Lines[diffLineIndex];
        // Comment on the right (new) side for context/added lines, left for deletions.
        var rightSide = line.Kind != DiffLineKind.Removed;
        var lineNumber = rightSide ? line.NewLineNumber : line.OldLineNumber;
        if (lineNumber is null)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await source.AddLineCommentAsync(
                pr.ProjectName, pr.RepositoryId, pr.PullRequestId, SelectedFile.Path, lineNumber.Value, rightSide, text, ct)
                .ConfigureAwait(false);
            Threads = await source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct).ConfigureAwait(false);
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
        Changed?.Invoke();
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
        Changed?.Invoke();
        try
        {
            await mutate().ConfigureAwait(false);
            Threads = await source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct).ConfigureAwait(false);
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
        // Assigned together with no await between: the view pairs the path with the diff to render
        // the header, so a drift would show one file's path against another file's stats.
        CurrentDiff = diff;
        CurrentDiffPath = diff is null ? null : file?.Path;
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
            _inflight.TryRemove(file.Path, out _);
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
