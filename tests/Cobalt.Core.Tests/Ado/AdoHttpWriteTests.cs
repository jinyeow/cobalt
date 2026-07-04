using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class AdoHttpWriteTests
{
    private const string UserJson =
        """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""";

    private static AdoHttp Client(FakeHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") });

    [Fact]
    public async Task Post_Serializes_Body_And_Deserializes_Response()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, UserJson);
        var body = new ConnectionData { AuthenticatedUser = new ConnectionAuthenticatedUser { Id = Guid.NewGuid() } };

        var result = await Client(handler).SendJsonAsync(
            HttpMethod.Post, "_apis/echo", body,
            AdoJsonContext.Default.ConnectionData, AdoJsonContext.Default.ConnectionData,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("authenticatedUser", handler.RequestBodies[0]);
        Assert.Equal("Jin", result.AuthenticatedUser?.ProviderDisplayName);
    }

    [Fact]
    public async Task ContentType_Override_Flows_Through_For_Json_Patch()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, UserJson);

        await Client(handler).SendJsonAsync(
            HttpMethod.Patch, "_apis/wit/workitems/1", new ConnectionData(),
            AdoJsonContext.Default.ConnectionData, AdoJsonContext.Default.ConnectionData,
            contentType: "application/json-patch+json",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("application/json-patch+json", handler.ContentTypes[0]);
    }

    [Fact]
    public async Task Status_203_SignIn_Page_Maps_To_Unauthorized()
    {
        var handler = new FakeHttpHandler().Respond(_ =>
            new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent("<html>sign in</html>"),
            });

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Null_Json_Success_Body_Throws_Empty_Response()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "null");

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"typeKey":"SomeException"}""")] // object without message
    [InlineData("""{"message":42}""")] // non-string message
    [InlineData("{not json at all")] // malformed
    public async Task Error_Envelope_Fallbacks_Report_Status_Line(string body)
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.BadRequest, body);

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Contains("400", ex.Message);
    }
}
