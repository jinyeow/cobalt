using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class AdoHttpTests
{
    private static AdoHttp Client(FakeHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") });

    [Fact]
    public async Task GetJson_Deserializes_CamelCase_Payload()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"authenticatedUser":{"id":"5e2c1a2b-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");

        var data = await Client(handler).GetJsonAsync(
            "_apis/connectionData?api-version=7.2-preview.1",
            AdoJsonContext.Default.ConnectionData,
            TestContext.Current.CancellationToken);

        Assert.Equal("Jin", data.AuthenticatedUser?.ProviderDisplayName);
    }

    [Fact]
    public async Task Relative_Path_Combines_With_Org_Base_Address()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");

        await Client(handler).GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/connectionData",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task NonSuccess_With_Ado_Error_Body_Throws_With_Message()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotFound,
            """{"message":"TF401232: Work item 999 does not exist.","typeKey":"WorkItemNotFoundException"}""");

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/wit/workitems/999", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("TF401232", ex.Message);
    }

    [Fact]
    public async Task NonSuccess_With_Html_Body_Reports_Status_Not_Garbage()
    {
        var handler = new FakeHttpHandler().Respond(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("<html>Sign in</html>"),
            });

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("<html>", ex.Message);
    }
}
