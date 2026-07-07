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
    private readonly HashSet<string> _viewed = new(StringComparer.Ordinal);
    private PrIteration? _iteration;
    private int _selectedFileIndex;

    public int PrId => pr.PullRequestId;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<FileChange> Files { get; private set; } = [];
    public IReadOnlyList<PrThread> Threads { get; private set; } = [];
    public FileDiff? CurrentDiff { get; private set; }

    public event Action? Changed;

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

    public bool IsViewed(string path) => _viewed.Contains(path);

    /// <summary>Additions/deletions for a file whose diff has already been computed; null otherwise.</summary>
    public (int Additions, int Deletions)? StatsFor(string path) =>
        _diffCache.TryGetValue(path, out var diff) ? (diff.Additions, diff.Deletions) : null;

    public int TotalAdditions => _diffCache.Values.Sum(d => d.Additions);
    public int TotalDeletions => _diffCache.Values.Sum(d => d.Deletions);

    /// <summary>
    /// Computes every file's diff sequentially into the cache (so <see cref="StatsFor"/> and the
    /// totals fill in), raising <see cref="Changed"/> after each so the UI can update lazily.
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
            Changed?.Invoke();
        }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
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
            Threads = await source.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct).ConfigureAwait(false);

            _selectedFileIndex = 0;
            if (Files.Count > 0)
            {
                await ComputeCurrentDiffAsync(ct).ConfigureAwait(false);
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
            IsLoading = false;
            Changed?.Invoke();
        }
    }

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
        CurrentDiff = file is null ? null : await ComputeDiffForFileAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>Computes (or returns the cached) diff for one file. Does not touch <see cref="CurrentDiff"/>.</summary>
    private async Task<FileDiff?> ComputeDiffForFileAsync(FileChange file, CancellationToken ct)
    {
        if (_iteration is null)
        {
            return null;
        }
        if (_diffCache.TryGetValue(file.Path, out var cached))
        {
            return cached;
        }

        // Added files have no base version; deleted files have no source version.
        // Renamed/moved files have their base blob at the old path.
        var basePath = file.OriginalPath ?? file.Path;
        var baseText = file.ChangeType == FileChangeKind.Add
            ? ""
            : await source.GetFileContentAsync(pr.ProjectName, pr.RepositoryId, basePath, _iteration.BaseCommitId ?? "", ct).ConfigureAwait(false);
        var sourceText = file.ChangeType == FileChangeKind.Delete
            ? ""
            : await source.GetFileContentAsync(pr.ProjectName, pr.RepositoryId, file.Path, _iteration.SourceCommitId ?? "", ct).ConfigureAwait(false);

        var diff = DiffService.Unified(baseText, sourceText);
        _diffCache[file.Path] = diff;
        return diff;
    }
}
