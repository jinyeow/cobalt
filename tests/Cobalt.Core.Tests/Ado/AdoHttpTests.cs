using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class AdoHttpTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private AdoHttp Client(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new AdoHttp(httpClient);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task WarmUp_Requests_The_ConnectionData_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"authenticatedUser":{}}""");

        await Client(handler).WarmUpAsync(TestContext.Current.CancellationToken);

        // The warm-up exists to pay DNS + TCP + TLS before the first real call, so it must actually
        // reach the org over the same client — and on a route that answers 200, not 404.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/connectionData?api-version=7.2-preview.1",
            request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task WarmUp_Swallows_An_Expected_Failure()
    {
        var handler = new FakeHttpHandler().Respond(_ => throw new HttpRequestException("no dns"));

        // Callers fire-and-forget the warm-up, so a throw here would surface as a phantom
        // crash-log entry with no message bar. The first real call reports the same fault properly.
        await Client(handler).WarmUpAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WarmUp_Swallows_An_Auth_Failure_Rather_Than_Surfacing_It()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, """{"message":"TF400813: not authorized"}""");

        // A warm-up firing before the user has a usable token must stay invisible: the real
        // sign-in path owns that message.
        await Client(handler).WarmUpAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WarmUp_Swallows_The_Quit_Race_On_A_Disposed_Connection()
    {
        var httpClient = new HttpClient(new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}"))
        {
            BaseAddress = new Uri("https://dev.azure.com/contoso/"),
        };
        var http = new AdoHttp(httpClient);
        httpClient.Dispose(); // the app quit while the warm-up was still in flight

        await http.WarmUpAsync(TestContext.Current.CancellationToken);
    }

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

    // ---- NET-1: stream-deserialize the success path; string only on the error path ----

    [Fact]
    public async Task Success_Body_Is_Deserialized()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"authenticatedUser":{"id":"5e2c1a2b-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");

        var data = await Client(handler).GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken);

        Assert.Equal("Jin", data.AuthenticatedUser?.ProviderDisplayName);
    }

    [Fact]
    public async Task Non_Authoritative_203_Maps_To_Unauthorized()
    {
        // ADO answers 203 + an HTML sign-in page (not 401) when the token is bad; that mapping
        // must survive the stream refactor and never try to JSON-parse the HTML.
        var handler = new FakeHttpHandler().Respond(_ =>
            new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent("<html>Sign in</html>"),
            });

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Empty_Success_Body_Throws_Empty_Response()
    {
        // A 200 whose body deserializes to null (a literal JSON null) is an empty response, not a
        // valid value; the stream path must keep surfacing it as such rather than returning null.
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "null");

        var ex = await Assert.ThrowsAsync<AdoApiException>(() =>
            Client(handler).GetJsonAsync(
                "_apis/x", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Contains("empty response body", ex.Message);
    }
}
