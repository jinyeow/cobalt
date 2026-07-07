using System.Text.Json.Serialization;

namespace Cobalt.Core.Models;

// ---- wire DTOs (Core / Teams) ----

public sealed record WebApiTeamListResult
{
    public IReadOnlyList<WebApiTeamDto> Value { get; init; } = [];
}

public sealed record WebApiTeamDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public Guid ProjectId { get; init; }
    public string ProjectName { get; init; } = "";
}

public sealed record TeamMemberListResult
{
    public IReadOnlyList<TeamMemberDto> Value { get; init; } = [];
}

public sealed record TeamMemberDto
{
    public IdentityRefDto? Identity { get; init; }
}

// ---- domain projection ----

/// <summary>A team the signed-in user belongs to (Core / Teams - Get All Teams).</summary>
public sealed record AdoTeam(Guid Id, string Name, Guid ProjectId, string ProjectName);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebApiTeamListResult))]
[JsonSerializable(typeof(TeamMemberListResult))]
public sealed partial class TeamsJsonContext : JsonSerializerContext;
