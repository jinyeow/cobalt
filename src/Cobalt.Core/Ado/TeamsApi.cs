using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>
/// Team membership reads (Core / Teams). Both routes are org-level on
/// <c>dev.azure.com</c>, so they ride the org-scoped <see cref="AdoHttp"/>.
/// </summary>
public sealed class TeamsApi(AdoHttp http)
{
    private const string TeamsApiVersion = "api-version=7.1-preview.3";
    private const string MembersApiVersion = "api-version=7.1";
    private const int ListTop = 200;

    /// <summary>Every team the caller belongs to across the org (Get All Teams, <c>$mine=true</c>).</summary>
    public async Task<IReadOnlyList<AdoTeam>> GetMyTeamsAsync(CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"_apis/teams?$mine=true&$top={ListTop}&{TeamsApiVersion}",
            TeamsJsonContext.Default.WebApiTeamListResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Value.Select(t => new AdoTeam(t.Id, t.Name, t.ProjectId, t.ProjectName))];
    }

    /// <summary>The members of one team (Get Team Members).</summary>
    public async Task<IReadOnlyList<AdoUser>> GetTeamMembersAsync(
        Guid projectId, Guid teamId, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"_apis/projects/{projectId}/teams/{teamId}/members?{MembersApiVersion}",
            TeamsJsonContext.Default.TeamMemberListResult,
            cancellationToken).ConfigureAwait(false);
        return
        [
            .. result.Value
                .Where(m => m.Identity is not null)
                .Select(m => new AdoUser(
                    Guid.TryParse(m.Identity!.Id, out var id) ? id : Guid.Empty,
                    m.Identity.DisplayName ?? m.Identity.UniqueName ?? "",
                    m.Identity.UniqueName)),
        ];
    }
}
