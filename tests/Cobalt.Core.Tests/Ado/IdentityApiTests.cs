using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class IdentityApiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private IdentityApi Api(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new IdentityApi(new AdoHttp(httpClient));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task GetAuthenticatedUser_Parses_ConnectionData()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {
              "authenticatedUser": {
                "id": "1f2e3d4c-0000-1111-2222-333344445555",
                "providerDisplayName": "Jin Yeow",
                "customDisplayName": "jin",
                "descriptor": "aad.abc123"
              }
            }
            """);
        var api = Api(handler);

        var user = await api.GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Guid.Parse("1f2e3d4c-0000-1111-2222-333344445555"), user.Id);
        Assert.Equal("jin", user.DisplayName); // customDisplayName wins when present
        Assert.Contains("api-version=", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task DisplayName_Falls_Back_To_ProviderDisplayName()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin Yeow"}}""");
        var api = Api(handler);

        var user = await api.GetAuthenticatedUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jin Yeow", user.DisplayName);
    }

    [Fact]
    public async Task Missing_AuthenticatedUser_Throws()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var api = Api(handler);

        await Assert.ThrowsAsync<AdoApiException>(
            () => api.GetAuthenticatedUserAsync(TestContext.Current.CancellationToken));
    }
}
