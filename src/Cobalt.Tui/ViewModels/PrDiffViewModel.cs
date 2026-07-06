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
}

/// <summary>
/// Diff review for a PR's latest iteration: changed-file list, per-file unified
/// diff (fetched lazily and cached), thread markers mapped to lines, and adding
/// line comments with the correct left/right anchor (SPEC §3, M5). UI-free.
/// </summary>
public sealed class PrDiffViewModel(IPrDiffSource source, PullRequest pr)
{
    private readonly Dictionary<string, FileDiff> _diffCache = new(StringComparer.Ordinal);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (AdoExceptions.IsExpected(ex))
        {
            Error = ex.Message;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (AdoExceptions.IsExpected(ex))
        {
            Error = ex.Message;
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
        if (file is null || _iteration is null)
        {
            CurrentDiff = null;
            return;
        }
        if (_diffCache.TryGetValue(file.Path, out var cached))
        {
            CurrentDiff = cached;
            return;
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

        CurrentDiff = DiffService.Unified(baseText, sourceText);
        _diffCache[file.Path] = CurrentDiff;
    }
}
