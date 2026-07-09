using System.Text.Json.Serialization;

namespace Cobalt.Core.Models;

/// <summary>Azure DevOps reviewer vote values (the numeric API contract).</summary>
public enum PrVote
{
    Rejected = -10,
    WaitingForAuthor = -5,
    NoVote = 0,
    ApprovedWithSuggestions = 5,
    Approved = 10,
}

public enum PrThreadStatus
{
    Unknown,
    Active,
    Fixed,
    WontFix,
    Closed,
    Pending,
    ByDesign,
}

public enum PrListFilter
{
    ReviewQueue,
    Team,
    Mine,
    Active,
}

// ---- wire DTOs ----

public sealed record PullRequestListResult
{
    public IReadOnlyList<PullRequestDto> Value { get; init; } = [];
}

public sealed record PullRequestDto
{
    public int PullRequestId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string Status { get; init; } = "";
    public bool IsDraft { get; init; }
    public string SourceRefName { get; init; } = "";
    public string TargetRefName { get; init; } = "";
    public string? MergeStatus { get; init; }
    public IdentityRefDto? CreatedBy { get; init; }
    public GitRepositoryDto? Repository { get; init; }
    public IReadOnlyList<ReviewerDto>? Reviewers { get; init; }
    public IReadOnlyList<ResourceRefDto>? WorkItemRefs { get; init; }
    public GitCommitRefDto? LastMergeSourceCommit { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
}

public sealed record GitRepositoryDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The owning project — populated on org-wide list responses; may be null.</summary>
    public TeamProjectRefDto? Project { get; init; }
}

public sealed record TeamProjectRefDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

public sealed record ReviewerDto
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public int Vote { get; init; }
    public bool IsRequired { get; init; }
}

public sealed record ResourceRefDto
{
    public string? Id { get; init; }
    public string? Url { get; init; }
}

public sealed record PrThreadListResult
{
    public IReadOnlyList<PrThreadDto> Value { get; init; } = [];
}

public sealed record PrThreadDto
{
    public int Id { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<PrCommentDto>? Comments { get; init; }
    public PrThreadContextDto? ThreadContext { get; init; }
}

public sealed record PrThreadContextDto
{
    public string? FilePath { get; init; }
    public PrCommentPositionDto? RightFileStart { get; init; }
    public PrCommentPositionDto? RightFileEnd { get; init; }
    public PrCommentPositionDto? LeftFileStart { get; init; }
    public PrCommentPositionDto? LeftFileEnd { get; init; }
}

public sealed record PrCommentPositionDto
{
    public int Line { get; init; }
    public int Offset { get; init; }
}

public sealed record PrCommentDto
{
    public int Id { get; init; }
    public int ParentCommentId { get; init; }
    public string Content { get; init; } = "";
    public IdentityRefDto? Author { get; init; }
    public string CommentType { get; init; } = "text";
}

// ---- request DTOs ----

public sealed record VoteRequest
{
    public required int Vote { get; init; }
}

public sealed record ThreadStatusPatch
{
    public required string Status { get; init; }
}

public sealed record PrStatusPatch
{
    public required string Status { get; init; }
    public GitCommitRefDto? LastMergeSourceCommit { get; init; }
    public CompletionOptionsDto? CompletionOptions { get; init; }
}

public sealed record GitCommitRefDto
{
    public string? CommitId { get; init; }
}

public sealed record CompletionOptionsDto
{
    public string? MergeStrategy { get; init; }
    public bool DeleteSourceBranch { get; init; }
}

public sealed record NewCommentRequest
{
    public required string Content { get; init; }
    public int ParentCommentId { get; init; }
    public int CommentType { get; init; } = 1; // 1 = text
}

public sealed record NewThreadRequest
{
    public required IReadOnlyList<NewCommentRequest> Comments { get; init; }
    public required int Status { get; init; }
    public PrThreadContextDto? ThreadContext { get; init; }
}

// ---- diff / iteration DTOs ----

public sealed record PrIterationListResult
{
    public IReadOnlyList<PrIterationDto> Value { get; init; } = [];
}

public sealed record PrIterationDto
{
    public int Id { get; init; }
    public GitCommitRefDto? SourceRefCommit { get; init; }
    public GitCommitRefDto? TargetRefCommit { get; init; }
    public GitCommitRefDto? CommonRefCommit { get; init; }
}

public sealed record PrIterationChangesResult
{
    public IReadOnlyList<PrChangeEntryDto> ChangeEntries { get; init; } = [];
}

public sealed record PrChangeEntryDto
{
    public string ChangeType { get; init; } = "";
    public GitItemDto? Item { get; init; }
    public string? SourceServerItem { get; init; }
    public string? OriginalPath { get; init; }
}

public sealed record GitItemDto
{
    public string? Path { get; init; }
    public bool IsFolder { get; init; }
}

// ---- diff domain projections ----

public enum FileChangeKind
{
    Add,
    Edit,
    Delete,
    Rename,
    Unknown,
}

public sealed record PrIteration(int Id, string? SourceCommitId, string? TargetCommitId, string? BaseCommitId);

public sealed record FileChange(string Path, FileChangeKind ChangeType, string? OriginalPath = null)
{
    public static FileChangeKind ParseKind(string changeType)
    {
        // ADO sends composite flags like "edit, rename"; take the most meaningful.
        var lower = changeType.ToLowerInvariant();
        if (lower.Contains("delete"))
        {
            return FileChangeKind.Delete;
        }
        if (lower.Contains("add"))
        {
            return FileChangeKind.Add;
        }
        if (lower.Contains("rename"))
        {
            return FileChangeKind.Rename;
        }
        if (lower.Contains("edit"))
        {
            return FileChangeKind.Edit;
        }
        return FileChangeKind.Unknown;
    }
}

// ---- domain projections ----

public sealed record PrReviewer(string Id, string DisplayName, PrVote Vote, bool IsRequired);

public sealed record PullRequest(
    int PullRequestId,
    string Title,
    string? Description,
    string Status,
    bool IsDraft,
    string SourceBranch,
    string TargetBranch,
    string? MergeStatus,
    string Author,
    string RepositoryId,
    string RepositoryName,
    IReadOnlyList<PrReviewer> Reviewers,
    IReadOnlyList<long> LinkedWorkItemIds,
    string? LastMergeSourceCommitId,
    string ProjectName = "",
    DateTimeOffset? CreationDate = null,
    string AuthorId = "",
    string ProjectId = "")
{
    /// <summary>
    /// Projects a wire DTO. <paramref name="fallbackProject"/> supplies the project
    /// name when the repository's <c>project</c> is absent (e.g. project-scoped list
    /// responses that omit it) — pass the context's project.
    /// </summary>
    public static PullRequest From(PullRequestDto dto, string? fallbackProject = null) => new(
        dto.PullRequestId,
        dto.Title,
        dto.Description,
        dto.Status,
        dto.IsDraft,
        ShortBranch(dto.SourceRefName),
        ShortBranch(dto.TargetRefName),
        dto.MergeStatus,
        dto.CreatedBy?.DisplayName ?? "unknown",
        dto.Repository?.Id ?? "",
        dto.Repository?.Name ?? "",
        [.. (dto.Reviewers ?? []).Select(r => new PrReviewer(
            r.Id ?? "", r.DisplayName ?? "?", ToVote(r.Vote), r.IsRequired))],
        [.. (dto.WorkItemRefs ?? []).Select(w => long.TryParse(w.Id, out var id) ? id : 0L).Where(id => id != 0)],
        dto.LastMergeSourceCommit?.CommitId,
        dto.Repository?.Project?.Name ?? fallbackProject ?? "",
        dto.CreationDate,
        dto.CreatedBy?.Id ?? "",
        dto.Repository?.Project?.Id ?? "");

    private static string ShortBranch(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    private static PrVote ToVote(int vote) => Enum.IsDefined((PrVote)vote) ? (PrVote)vote : PrVote.NoVote;
}

public sealed record PrComment(int Id, string Author, string Content, bool IsSystem);

public sealed record PrThread(
    int Id,
    PrThreadStatus Status,
    IReadOnlyList<PrComment> Comments,
    string? FilePath,
    int? RightLine,
    int? LeftLine)
{
    public static PrThread From(PrThreadDto dto) => new(
        dto.Id,
        ParseStatus(dto.Status),
        [.. (dto.Comments ?? []).Select(c => new PrComment(
            c.Id, c.Author?.DisplayName ?? "?", c.Content, c.CommentType == "system"))],
        dto.ThreadContext?.FilePath,
        dto.ThreadContext?.RightFileStart?.Line,
        dto.ThreadContext?.LeftFileStart?.Line);

    public bool IsSystemOnly => Comments.All(c => c.IsSystem);

    public static PrThreadStatus ParseStatus(string? status) => status switch
    {
        "active" => PrThreadStatus.Active,
        "fixed" => PrThreadStatus.Fixed,
        "wontFix" => PrThreadStatus.WontFix,
        "closed" => PrThreadStatus.Closed,
        "pending" => PrThreadStatus.Pending,
        "byDesign" => PrThreadStatus.ByDesign,
        _ => PrThreadStatus.Unknown,
    };

    public static string StatusToWire(PrThreadStatus status) => status switch
    {
        PrThreadStatus.Active => "active",
        PrThreadStatus.Fixed => "fixed",
        PrThreadStatus.WontFix => "wontFix",
        PrThreadStatus.Closed => "closed",
        PrThreadStatus.Pending => "pending",
        PrThreadStatus.ByDesign => "byDesign",
        _ => "active",
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PullRequestListResult))]
[JsonSerializable(typeof(PullRequestDto))]
[JsonSerializable(typeof(PrThreadListResult))]
[JsonSerializable(typeof(PrThreadDto))]
[JsonSerializable(typeof(PrCommentDto))]
[JsonSerializable(typeof(ReviewerDto))]
[JsonSerializable(typeof(VoteRequest))]
[JsonSerializable(typeof(ThreadStatusPatch))]
[JsonSerializable(typeof(PrStatusPatch))]
[JsonSerializable(typeof(NewCommentRequest))]
[JsonSerializable(typeof(NewThreadRequest))]
[JsonSerializable(typeof(PrIterationListResult))]
[JsonSerializable(typeof(PrIterationChangesResult))]
public sealed partial class GitJsonContext : JsonSerializerContext;
