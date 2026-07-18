using System.Text.Json;
using System.Text.Json.Serialization;
using Cobalt.Core.Text;

namespace Cobalt.Core.Models;

// ---- wire DTOs (match the ADO REST shapes) ----

public sealed record WiqlResult
{
    public IReadOnlyList<WiqlWorkItemRef> WorkItems { get; init; } = [];
}

public sealed record WiqlWorkItemRef
{
    public long Id { get; init; }
}

public sealed record WiqlQuery
{
    public required string Query { get; init; }
}

public sealed record WorkItemBatchRequest
{
    public required IReadOnlyList<long> Ids { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
}

public sealed record WorkItemBatchResult
{
    public IReadOnlyList<WorkItemDto> Value { get; init; } = [];
}

public sealed record WorkItemDto
{
    public long Id { get; init; }
    public Dictionary<string, JsonElement> Fields { get; init; } = [];
}

public sealed record WorkItemStatesResult
{
    public IReadOnlyList<WorkItemStateDto> Value { get; init; } = [];
}

public sealed record WorkItemStateDto
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed record WorkItemCommentsResult
{
    public IReadOnlyList<WorkItemCommentDto> Comments { get; init; } = [];
}

public sealed record WorkItemCommentDto
{
    public long Id { get; init; }
    public string Text { get; init; } = "";
    public IdentityRefDto? CreatedBy { get; init; }
    public DateTimeOffset CreatedDate { get; init; }
}

public sealed record AddCommentRequest
{
    public required string Text { get; init; }
}

public sealed record IdentityRefDto
{
    public string? DisplayName { get; init; }
    public string? UniqueName { get; init; }
    public string? Id { get; init; }
}

// ---- domain projections (what the UI binds to) ----

public sealed class WorkItem
{
    private readonly IReadOnlyDictionary<string, JsonElement> _fields;

    public WorkItem(long id, IReadOnlyDictionary<string, JsonElement> fields)
    {
        Id = id;
        _fields = fields;

        // Project the list-row fields once: these are read on every list render, so materialize
        // them in the ctor instead of re-walking the JsonElement dict per property access. Detail
        // fields (below) stay lazy against the retained raw dict.
        Title = GetString("System.Title");
        State = GetString("System.State");
        WorkItemType = GetString("System.WorkItemType");
        IterationPath = GetString("System.IterationPath");
        TeamProject = GetString("System.TeamProject");
        AssignedToDisplayName = GetIdentityDisplayName("System.AssignedTo");
        AssignedToUniqueName = GetIdentityUniqueName("System.AssignedTo");
        ChangedDate = GetDate("System.ChangedDate");

        var rawTags = GetString("System.Tags");
        Tags = rawTags.Length == 0
            ? []
            : rawTags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public long Id { get; }

    public string Title { get; }
    public string State { get; }
    public string WorkItemType { get; }
    public string IterationPath { get; }
    public string TeamProject { get; }
    public string? AssignedToDisplayName { get; }
    public string? AssignedToUniqueName { get; }
    public DateTimeOffset? ChangedDate { get; }
    public IReadOnlyList<string> Tags { get; }

    // Detail reads: fetched only for the detail pane, so left lazy against the raw dict.
    public string AreaPath => GetString("System.AreaPath");
    public string DescriptionHtml => GetString("System.Description");
    public int? Priority => GetInt("Microsoft.VSTS.Common.Priority");
    public double? StoryPoints => GetDouble("Microsoft.VSTS.Scheduling.StoryPoints");

    public string GetString(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

    private int? GetInt(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private double? GetDouble(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private DateTimeOffset? GetDate(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.String &&
        e.TryGetDateTimeOffset(out var d) ? d : null;

    private string? GetIdentityDisplayName(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty("displayName", out var n) ? n.GetString() : null;

    private string? GetIdentityUniqueName(string field) =>
        _fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty("uniqueName", out var n) ? n.GetString() : null;

    public static WorkItem From(WorkItemDto dto) => new(dto.Id, dto.Fields);
}

public sealed record WorkItemComment(long Id, string Author, DateTimeOffset CreatedDate, string TextMarkdown)
{
    public static WorkItemComment From(WorkItemCommentDto dto) => new(
        dto.Id,
        dto.CreatedBy?.DisplayName ?? "unknown",
        dto.CreatedDate,
        HtmlMarkdown.ToMarkdown(dto.Text));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WiqlResult))]
[JsonSerializable(typeof(WiqlQuery))]
[JsonSerializable(typeof(WorkItemBatchRequest))]
[JsonSerializable(typeof(WorkItemBatchResult))]
[JsonSerializable(typeof(WorkItemDto))]
[JsonSerializable(typeof(WorkItemStatesResult))]
[JsonSerializable(typeof(WorkItemCommentsResult))]
[JsonSerializable(typeof(WorkItemCommentDto))]
[JsonSerializable(typeof(AddCommentRequest))]
public sealed partial class WorkItemJsonContext : JsonSerializerContext;
