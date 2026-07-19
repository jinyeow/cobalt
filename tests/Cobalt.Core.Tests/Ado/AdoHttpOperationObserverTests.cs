using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class AdoHttpOperationObserverTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private HttpClient HttpClient(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return httpClient;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task GetJson_Reports_One_Operation_With_Name_Duration_And_Status()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"authenticatedUser":{}}""");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await http.GetJsonAsync(
            "_apis/wit/workitems/999?api-version=7.2-preview.1",
            AdoJsonContext.Default.ConnectionData,
            TestContext.Current.CancellationToken);

        var op = Assert.Single(reported);
        Assert.Equal("GET", op.Name);
        Assert.Equal("_apis/wit/workitems/{id}?api-version=7.2-preview.1", op.RouteShape);
        Assert.Equal(200, op.Status);
        Assert.True(op.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Observer_Set_Via_Property_After_Construction_Still_Fires()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler)) { OperationObserver = reported.Add };

        await http.GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken);

        Assert.Single(reported);
    }

    [Fact]
    public async Task NonSuccess_Response_Still_Reports_Its_Status()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotFound, """{"message":"not found"}""");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await Assert.ThrowsAsync<AdoApiException>(() => http.GetJsonAsync(
            "_apis/wit/workitems/999", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        var op = Assert.Single(reported);
        Assert.Equal(404, op.Status);
    }

    [Fact]
    public async Task Without_An_Observer_No_Extra_Work_Happens_And_Requests_Still_Succeed()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var http = new AdoHttp(HttpClient(handler));

        await http.GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendRaw_With_An_Absolute_Url_Never_Reports_The_Raw_Credentialed_String()
    {
        // The Uri(path, UriKind.Relative) constructor throws when path is absolute-looking,
        // so this exercises the "request never went out" path — the observer must still see a
        // redacted route, never the raw string that was passed in.
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);
        const string credentialedUrl = "https://user:super-secret-pat@dev.azure.com/contoso/_apis/x?sig=SIGSECRET";

        await Assert.ThrowsAnyAsync<Exception>(() => http.SendRawAsync(
            HttpMethod.Patch, credentialedUrl, "{}",
            AdoJsonContext.Default.ConnectionData, cancellationToken: TestContext.Current.CancellationToken));

        var op = Assert.Single(reported);
        Assert.DoesNotContain("super-secret-pat", op.RouteShape);
        Assert.DoesNotContain("SIGSECRET", op.RouteShape);
        Assert.DoesNotContain("@", op.RouteShape);
        Assert.DoesNotContain("dev.azure.com", op.RouteShape);
        Assert.Null(op.Status);
    }

    [Fact]
    public async Task SendJson_Reports_Exactly_One_Operation()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"authenticatedUser":{}}""");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await http.SendJsonAsync(
            HttpMethod.Post, "_apis/echo", new ConnectionData(),
            AdoJsonContext.Default.ConnectionData, AdoJsonContext.Default.ConnectionData,
            cancellationToken: TestContext.Current.CancellationToken);

        var op = Assert.Single(reported);
        Assert.Equal("POST", op.Name);
        Assert.Equal(200, op.Status);
    }

    [Fact]
    public async Task SendRaw_Reports_Exactly_One_Operation()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await http.SendRawAsync(
            HttpMethod.Patch, "_apis/wit/workitems/1", "{}",
            AdoJsonContext.Default.ConnectionData, cancellationToken: TestContext.Current.CancellationToken);

        var op = Assert.Single(reported);
        Assert.Equal("PATCH", op.Name);
        Assert.Equal(200, op.Status);
    }

    [Fact]
    public async Task GetTextOrNull_Reports_Exactly_One_Operation()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "raw text");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await http.GetTextOrNullAsync("_apis/git/blob", TestContext.Current.CancellationToken);

        var op = Assert.Single(reported);
        Assert.Equal("GET", op.Name);
        Assert.Equal(200, op.Status);
    }

    [Fact]
    public async Task A_Transport_Exception_Still_Reports_One_Operation_With_A_Null_Status()
    {
        var handler = new FakeHttpHandler().Respond(_ => throw new HttpRequestException("no dns"));
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        var op = Assert.Single(reported);
        Assert.Null(op.Status);
    }

    [Fact]
    public async Task A_Throwing_Observer_Does_Not_Mask_A_Successful_Request()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"authenticatedUser":{"providerDisplayName":"Jin"}}""");
        var http = new AdoHttp(HttpClient(handler), operationObserver: _ => throw new InvalidOperationException("boom"));

        var data = await http.GetJsonAsync(
            "_apis/connectionData", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken);

        Assert.Equal("Jin", data.AuthenticatedUser?.ProviderDisplayName);
    }

    [Fact]
    public async Task A_Throwing_Observer_Does_Not_Mask_The_Requests_Real_Failure()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotFound, """{"message":"TF401232: missing"}""");
        var http = new AdoHttp(HttpClient(handler), operationObserver: _ => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<AdoApiException>(() => http.GetJsonAsync(
            "_apis/wit/workitems/999", AdoJsonContext.Default.ConnectionData, TestContext.Current.CancellationToken));

        Assert.Contains("TF401232", ex.Message);
    }

    [Fact]
    public async Task WarmUp_Does_Not_Report_An_Operation()
    {
        // Deliberate (see the rationale on WarmUpAsync itself): the warm-up has no
        // user-visible job, so it stays out of the :log view — surfacing it would be noise
        // the user never asked to see.
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "{}");
        var reported = new List<AdoOperation>();
        var http = new AdoHttp(HttpClient(handler), operationObserver: reported.Add);

        await http.WarmUpAsync(TestContext.Current.CancellationToken);

        Assert.Empty(reported);
    }
}
