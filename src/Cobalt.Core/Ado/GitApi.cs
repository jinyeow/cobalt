using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>Pull-request reads and writes for one project (SPEC §3).</summary>
public sealed class GitApi(AdoHttp http, AdoContext context)
{
    private const string PrApiVersion = "api-version=7.2-preview.2";
    private const string ThreadApiVersion = "api-version=7.2-preview.1";
    private const string ReviewerApiVersion = "api-version=7.2-preview.1";

    private string Project => Uri.EscapeDataString(context.Project);

    public async Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(
        PrListFilter filter, Guid me, CancellationToken cancellationToken = default)
    {
        var criteria = filter switch
        {
            PrListFilter.ReviewQueue => $"searchCriteria.status=active&searchCriteria.reviewerId={me}",
            PrListFilter.Mine => $"searchCriteria.status=active&searchCriteria.creatorId={me}",
            _ => "searchCriteria.status=active",
        };

        var result = await http.GetJsonAsync(
            $"{Project}/_apis/git/pullrequests?{criteria}&$top=100&{PrApiVersion}",
            GitJsonContext.Default.PullRequestListResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Value.Select(PullRequest.From)];
    }

    public async Task<PullRequest> GetPullRequestAsync(int id, CancellationToken cancellationToken = default)
    {
        var dto = await http.GetJsonAsync(
            $"_apis/git/pullrequests/{id}?{PrApiVersion}",
            GitJsonContext.Default.PullRequestDto,
            cancellationToken).ConfigureAwait(false);
        return PullRequest.From(dto);
    }

    public async Task<IReadOnlyList<PrThread>> GetThreadsAsync(
        string repositoryId, int prId, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads?{ThreadApiVersion}",
            GitJsonContext.Default.PrThreadListResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Value.Select(PrThread.From).Where(t => !t.IsSystemOnly)];
    }

    public Task VoteAsync(
        string repositoryId, int prId, Guid reviewerId, PrVote vote, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Put,
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/reviewers/{reviewerId}?{ReviewerApiVersion}",
            new VoteRequest { Vote = (int)vote },
            GitJsonContext.Default.VoteRequest,
            GitJsonContext.Default.ReviewerDto,
            cancellationToken: cancellationToken);

    public Task<PrCommentDto> ReplyToThreadAsync(
        string repositoryId, int prId, int threadId, string content, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads/{threadId}/comments?{ThreadApiVersion}",
            new NewCommentRequest { Content = content },
            GitJsonContext.Default.NewCommentRequest,
            GitJsonContext.Default.PrCommentDto,
            cancellationToken: cancellationToken);

    public Task<PrThreadDto> AddThreadAsync(
        string repositoryId, int prId, NewThreadRequest thread, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads?{ThreadApiVersion}",
            thread,
            GitJsonContext.Default.NewThreadRequest,
            GitJsonContext.Default.PrThreadDto,
            cancellationToken: cancellationToken);

    public Task SetThreadStatusAsync(
        string repositoryId, int prId, int threadId, PrThreadStatus status, CancellationToken cancellationToken = default) =>
        http.SendJsonAsync(
            HttpMethod.Patch,
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}/threads/{threadId}?{ThreadApiVersion}",
            new ThreadStatusPatch { Status = PrThread.StatusToWire(status) },
            GitJsonContext.Default.ThreadStatusPatch,
            GitJsonContext.Default.PrThreadDto,
            cancellationToken: cancellationToken);

    public Task<PullRequest> AbandonAsync(
        string repositoryId, int prId, CancellationToken cancellationToken = default) =>
        PatchStatusAsync(repositoryId, prId, new PrStatusPatch { Status = "abandoned" }, cancellationToken);

    public Task<PullRequest> CompleteAsync(
        string repositoryId,
        int prId,
        string lastMergeSourceCommitId,
        string mergeStrategy,
        bool deleteSourceBranch,
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
        }, cancellationToken);

    private async Task<PullRequest> PatchStatusAsync(
        string repositoryId, int prId, PrStatusPatch patch, CancellationToken cancellationToken)
    {
        var dto = await http.SendJsonAsync(
            HttpMethod.Patch,
            $"{Project}/_apis/git/repositories/{Enc(repositoryId)}/pullRequests/{prId}?{PrApiVersion}",
            patch,
            GitJsonContext.Default.PrStatusPatch,
            GitJsonContext.Default.PullRequestDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return PullRequest.From(dto);
    }

    private static string Enc(string segment) => Uri.EscapeDataString(segment);
}
