using System.Net;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

public sealed class IdentityApi(AdoHttp http)
{
    /// <summary>Resolves the signed-in user via connectionData (needed for @Me and reviewer votes).</summary>
    public async Task<AdoUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        var data = await http.GetJsonAsync(
            "_apis/connectionData?api-version=7.2-preview.1",
            AdoJsonContext.Default.ConnectionData,
            cancellationToken).ConfigureAwait(false);

        var user = data.AuthenticatedUser;
        if (user is null || user.Id == Guid.Empty)
        {
            throw new AdoApiException(
                HttpStatusCode.Unauthorized,
                "Azure DevOps did not identify the signed-in user (token may lack the Azure DevOps scope)");
        }

        return new AdoUser(
            user.Id,
            user.CustomDisplayName ?? user.ProviderDisplayName ?? user.Id.ToString(),
            user.Descriptor);
    }
}
