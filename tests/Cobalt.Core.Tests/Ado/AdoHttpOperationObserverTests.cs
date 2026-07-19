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
}
