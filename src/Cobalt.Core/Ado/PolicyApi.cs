using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>Branch-policy / build-status evaluations for a pull request (Policy Evaluations API, SPEC §3).</summary>
public sealed class PolicyApi(AdoHttp http)
{
    private const string ApiVersion = "api-version=7.2-preview.1";

    /// <summary>
    /// Policy evaluations for PR <paramref name="prId"/> in project <paramref name="projectId"/>.
    /// The project GUID doubles as the route segment (ADO accepts an id or name there) and the
    /// project component of the CodeReviewId artifactId, which requires the GUID specifically.
    /// </summary>
    public async Task<IReadOnlyList<PolicyEvaluation>> GetEvaluationsAsync(
        string projectId, int prId, CancellationToken cancellationToken = default)
    {
        var artifactId = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{prId}";
        var result = await http.GetJsonAsync(
            $"{Uri.EscapeDataString(projectId)}/_apis/policy/evaluations" +
            $"?artifactId={Uri.EscapeDataString(artifactId)}&{ApiVersion}",
            PolicyJsonContext.Default.PolicyEvaluationListResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Value.Select(PolicyEvaluation.From)];
    }
}
