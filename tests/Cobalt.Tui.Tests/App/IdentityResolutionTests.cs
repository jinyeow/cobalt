using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// NET-2 wiring: the background identity resolve now goes through the single-flight
/// <c>AdoConnection.GetIdentityAsync</c> (primed by <c>PrimeIdentityAsync</c>) instead of the
/// old per-app <c>Lazy</c> + a separate <c>WarmUpAsync</c> ping — so cold start makes one
/// connectionData call, not two (the identity cache is Unit B's, tested there).
/// </summary>
public class IdentityResolutionTests
{
    [Fact]
    public async Task Success_Yields_The_Display_Name()
    {
        var user = new AdoUser(Guid.NewGuid(), "Jin Yeow", "aad.abc");

        var outcome = await CobaltTuiApp.ResolveIdentityAsync(
            _ => Task.FromResult(user), TestContext.Current.CancellationToken);

        Assert.Equal("Jin Yeow", outcome.DisplayName);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task Expected_Ado_Fault_Yields_An_Actionable_Error_Message()
    {
        var outcome = await CobaltTuiApp.ResolveIdentityAsync(
            _ => Task.FromException<AdoUser>(new AdoApiException(HttpStatusCode.Unauthorized, "TF400813: not authorized")),
            TestContext.Current.CancellationToken);

        Assert.Null(outcome.DisplayName);
        Assert.NotNull(outcome.ErrorMessage);
        Assert.Contains("not signed in", outcome.ErrorMessage);
        Assert.Contains("TF400813", outcome.ErrorMessage);
        Assert.Contains("cobalt auth login", outcome.ErrorMessage);
    }
}
