using System.Text.Json.Serialization;

namespace Cobalt.Core.Models;

/// <summary>A single branch-policy evaluation result for a pull request (Policy Evaluations API).</summary>
public sealed record PolicyEvaluation(string DisplayName, string Status, bool IsBlocking)
{
    /// <summary>Projects a wire evaluation record; missing configuration/type fields degrade to safe defaults.</summary>
    public static PolicyEvaluation From(PolicyEvaluationDto dto) => new(
        dto.Configuration?.Type?.DisplayName ?? "policy",
        dto.Status ?? "unknown",
        dto.Configuration?.IsBlocking ?? false);
}

// ---- wire DTOs ----

public sealed record PolicyEvaluationListResult
{
    public IReadOnlyList<PolicyEvaluationDto> Value { get; init; } = [];
}

public sealed record PolicyEvaluationDto
{
    public string? Status { get; init; }
    public PolicyConfigurationDto? Configuration { get; init; }
}

public sealed record PolicyConfigurationDto
{
    public bool IsBlocking { get; init; }
    public PolicyTypeRefDto? Type { get; init; }
}

public sealed record PolicyTypeRefDto
{
    public string? DisplayName { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PolicyEvaluationListResult))]
public sealed partial class PolicyJsonContext : JsonSerializerContext;
