using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>Pull-request reads and writes for one project (SPEC §3).</summary>
public sealed class GitApi(AdoHttp http, AdoContext context)
{
    private const string PrApiVersion = "api-version=7.2-preview.2";
    private const string ThreadApiVersion = "api-version=7.2-preview.1";
    private const string ReviewerApiVersion = "api-version=7.2-preview.1";

    private const int ListTop = 200;

    private string Project => Uri.EscapeDataString(context.Project);

    /// <summary>Path segment for a repo-scoped call: the PR's own project, or the context's.</summary>
    private string ProjectSeg(string? project) =>
        Uri.EscapeDataString(string.IsNullOrEmpty(project) ? context.Project : project);

    public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(
        PrListFilter filter, Guid me, PrScope scope, CancellationToken cancellationToken = default)
    {
        // Team is aggregated in the adapter from reviewer + author halves, not a single
        // searchCriteria query — the reviewer half delegates to ListPullRequestsForReviewerAsync.
        if (filter == PrListFilter.ReviewQueue)
        {
            return ListPullRequestsForReviewerAsync(me, scope, cancellationToken);
        }

        var criteria = filter == PrListFilter.Mine
            ? $"searchCriteria.status=active&searchCriteria.creatorId={me}"
            : "searchCriteria.status=active";
        return ListByCriteriaAsync(criteria, scope, cancellationToken);
    }

    /// <summary>
    /// Active PRs where <paramref name="reviewerId"/> is a requested reviewer. A team is an
    /// identity, so a team's Guid works here too — that's the Team tab's reviewer half.
    /// </summary>
    public Task<IReadOnlyList<PullRequest>> ListPullRequestsForReviewerAsync(
        Guid reviewerId, PrScope scope, CancellationToken cancellationToken = default) =>
        ListByCriteriaAsync(
            $"searchCriteria.status=active&searchCriteria.reviewerId={reviewerId}", scope, cancellationToken);

    private async Task<IReadOnlyList<PullRequest>> ListByCriteriaAsync(
        string criteria, PrScope scope, CancellationToken cancellationToken)
    {
        // Org scope hits the org-wide list route (no project segment); Project scope
        // keeps the classic per-project route. The org-level *list* endpoint is not
        // formally documented (unlike the org-level by-id and reviewer routes, which
        // are), so it's validated during UAT. Keeping the prefix a single local switch
        // makes a future swap to a documented per-project fan-out a localized change.
        var prefix = scope == PrScope.Org ? "" : $"{Project}/";
        var result = await http.GetJsonAsync(
            $"{prefix}_apis/git/pullrequests?{criteria}&$top={ListTop}&{PrApiVersion}",
            GitJsonContext.Default.PullRequestListResult,
            cancellationToken).ConfigureAwait(false);

        // Fall back to the context project when the repo carries no project (project-scoped
        // responses often omit it); org-scoped rows carry their own project.
        return [.. result.Value.Select(dto => PullRequest.From(dto, context.Project))];
    }

    public async Task<PullRequest> GetPullRequestAsync(int id, CancellationToken cancellationToken = default)
    {
        var dto = await http.GetJsonAsync(
            $"_apis/git/pullrequests/{id}?{PrApiVersion}",
            GitJsonContext.Default.PullRequestDto,
            cancellationToken).ConfigureAwait(false);
        // Fall back to the context project if the by-id payload omits repository.project, matching
        // the list path — so URL builders (gx/gb) don't drop to a blank project segment.
        return PullRequest.From(dto, context.Project);
    }

    public async Task<IReadOnlyList<PrThread>> GetThreadsAsync(
        string repositoryId, int prId, string? project = null, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads?{ThreadApiVersion}",
            GitJsonContext.Default.PrThreadListResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Value.Select(PrThread.From).Where(t => !t.IsSystemOnly)];
    }

    public Task VoteAsync(
        string repositoryId, int prId, Guid reviewerId, PrVote vote, string? project = null, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Put,
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/reviewers/{reviewerId}?{ReviewerApiVersion}",
            new VoteRequest { Vote = (int)vote },
            GitJsonContext.Default.VoteRequest,
            GitJsonContext.Default.ReviewerDto,
            cancellationToken: cancellationToken);

    public Task<PrCommentDto> ReplyToThreadAsync(
        string repositoryId, int prId, int threadId, string content, string? project = null, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads/{threadId}/comments?{ThreadApiVersion}",
            new NewCommentRequest { Content = content },
            GitJsonContext.Default.NewCommentRequest,
            GitJsonContext.Default.PrCommentDto,
            cancellationToken: cancellationToken);

    public Task<PrThreadDto> AddThreadAsync(
        string repositoryId, int prId, NewThreadRequest thread, string? project = null, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads?{ThreadApiVersion}",
            thread,
            GitJsonContext.Default.NewThreadRequest,
            GitJsonContext.Default.PrThreadDto,
            cancellationToken: cancellationToken);

    public Task SetThreadStatusAsync(
        string repositoryId, int prId, int threadId, PrThreadStatus status, string? project = null, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Patch,
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads/{threadId}?{ThreadApiVersion}",
            new ThreadStatusPatch { Status = PrThread.StatusToWire(status) },
            GitJsonContext.Default.ThreadStatusPatch,
            GitJsonContext.Default.PrThreadDto,
            cancellationToken: cancellationToken);

    public Task<PullRequest> AbandonAsync(
        string repositoryId, int prId, string? project = null, CancellationToken cancellationToken = default) =>
        PatchStatusAsync(repositoryId, prId, new PrStatusPatch { Status = "abandoned" }, project, cancellationToken);

    public Task<PullRequest> CompleteAsync(
        string repositoryId,
        int prId,
        string lastMergeSourceCommitId,
        string mergeStrategy,
        bool deleteSourceBranch,
        string? project = null,
        CancellationToken cancellationToken = default) =>
        PatchStatusAsync(repositoryId, prId, new PrStatusPatch
        {
            Status = "completed",
            LastMergeSourceCommit = new GitCommitRefDto { CommitId = lastMergeSourceCommitId },
            CompletionOptions = new CompletionOptionsDto
            {
                MergeStrategy = mergeStrategy,
                DeleteSourceBranch = deleteSourceBranch,
            },
        }, project, cancellationToken);

    private async Task<PullRequest> PatchStatusAsync(
        string repositoryId, int prId, PrStatusPatch patch, string? project, CancellationToken cancellationToken)
    {
        var dto = await http.SendJsonAsync(
            HttpMethod.Patch,
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}?{PrApiVersion}",
            patch,
            GitJsonContext.Default.PrStatusPatch,
            GitJsonContext.Default.PullRequestDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return PullRequest.From(dto, context.Project);
    }

    // ---- diff / review (SPEC §3, M5) ----

    public async Task<PrIteration?> GetLatestIterationAsync(
        string repositoryId, int prId, string? project = null, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/iterations?{PrApiVersion}",
            GitJsonContext.Default.PrIterationListResult,
            cancellationToken).ConfigureAwait(false);

        var latest = result.Value.MaxBy(i => i.Id);
        return latest is null
            ? null
            : new PrIteration(
                latest.Id,
                latest.SourceRefCommit?.CommitId,
                latest.TargetRefCommit?.CommitId,
                latest.CommonRefCommit?.CommitId);
    }

    public async Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(
        string repositoryId, int prId, int iterationId, string? project = null, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/iterations/{iterationId}/changes?{ThreadApiVersion}",
            GitJsonContext.Default.PrIterationChangesResult,
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. result.ChangeEntries
                .Where(c => c.Item?.Path is not null && !c.Item.IsFolder)
                .Select(c =>
                {
                    var path = c.Item!.Path!;
                    var original = c.SourceServerItem ?? c.OriginalPath;
                    // Normalize to null when it doesn't actually differ from the current path.
                    if (string.Equals(original, path, StringComparison.Ordinal))
                    {
                        original = null;
                    }
                    return new FileChange(path, FileChange.ParseKind(c.ChangeType), original);
                }),
        ];
    }

    /// <summary>File content at a commit; empty string when the file doesn't exist on that side.</summary>
    public async Task<string> GetFileContentAsync(
        string repositoryId, string path, string commitId, string? project = null, CancellationToken cancellationToken = default)
    {
        var query =
            $"path={Uri.EscapeDataString(path)}" +
            $"&versionDescriptor.version={Uri.EscapeDataString(commitId)}" +
            "&versionDescriptor.versionType=commit" +
            "&includeContent=true&$format=text&api-version=7.2-preview.1";
        var content = await http.GetTextOrNullAsync(
            $"{ProjectSeg(project)}/_apis/git/repositories/{Enc(repositoryId)}/items?{query}",
            cancellationToken).ConfigureAwait(false);
        return content ?? "";
    }

    public Task<PrThreadDto> AddLineCommentAsync(
        string repositoryId,
        int prId,
        string filePath,
        int line,
        bool rightSide,
        string content,
        string? project = null,
        CancellationToken cancellationToken = default)
    {
        var position = new PrCommentPositionDto { Line = line, Offset = 1 };
        var context = new PrThreadContextDto
        {
            FilePath = filePath,
            RightFileStart = rightSide ? position : null,
            RightFileEnd = rightSide ? position : null,
            LeftFileStart = rightSide ? null : position,
            LeftFileEnd = rightSide ? null : position,
        };
        var thread = new NewThreadRequest
        {
            Comments = [new NewCommentRequest { Content = content }],
            Status = 1, // active
            ThreadContext = context,
        };
        return AddThreadAsync(repositoryId, prId, thread, project, cancellationToken);
    }

    private static string Enc(string segment) => Uri.EscapeDataString(segment);
}
