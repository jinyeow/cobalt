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

public sealed class WorkItem(long id, IReadOnlyDictionary<string, JsonElement> fields)
{
    public long Id { get; } = id;

    public string Title => GetString("System.Title");
    public string State => GetString("System.State");
    public string WorkItemType => GetString("System.WorkItemType");
    public string IterationPath => GetString("System.IterationPath");
    public string AreaPath => GetString("System.AreaPath");
    public string TeamProject => GetString("System.TeamProject");
    public string? AssignedToDisplayName => GetIdentityDisplayName("System.AssignedTo");
    public string? AssignedToUniqueName => GetIdentityUniqueName("System.AssignedTo");
    public string DescriptionHtml => GetString("System.Description");
    public DateTimeOffset? ChangedDate => GetDate("System.ChangedDate");
    public int? Priority => GetInt("Microsoft.VSTS.Common.Priority");
    public double? StoryPoints => GetDouble("Microsoft.VSTS.Scheduling.StoryPoints");

    public IReadOnlyList<string> Tags
    {
        get
        {
            var raw = GetString("System.Tags");
            return raw.Length == 0
                ? []
                : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    public string GetString(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

    private int? GetInt(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private double? GetDouble(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    private DateTimeOffset? GetDate(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.String &&
        e.TryGetDateTimeOffset(out var d) ? d : null;

    private string? GetIdentityDisplayName(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty("displayName", out var n) ? n.GetString() : null;

    private string? GetIdentityUniqueName(string field) =>
        fields.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Object &&
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
