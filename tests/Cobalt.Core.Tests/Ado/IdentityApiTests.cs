using System.Net;
using System.Text;
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

    // ---- NET-2: reset-on-fault single-flight identity cache ----

    [Fact]
    public async Task GetIdentity_Caches_The_User_After_The_First_Call()
    {
        // Only one connectionData response is scripted: a second HTTP call would throw
        // "no scripted response", so a second request would fail the test.
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");
        var api = Api(handler);

        var first = await api.GetIdentityAsync(TestContext.Current.CancellationToken);
        var second = await api.GetIdentityAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(handler.Requests); // one connectionData call, not two
    }

    [Fact]
    public async Task GetIdentity_Collapses_Concurrent_Callers_Into_One_Request()
    {
        var handler = new GatedIdentityHandler(
            """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        var api = new IdentityApi(new AdoHttp(httpClient));

        // Both callers arrive while the first request is still gated in flight.
        var a = api.GetIdentityAsync(TestContext.Current.CancellationToken);
        var b = api.GetIdentityAsync(TestContext.Current.CancellationToken);
        handler.Release();
        await Task.WhenAll(a, b);

        Assert.Equal(1, handler.RequestCount); // single-flight: one request served both callers
    }

    [Fact]
    public async Task GetIdentity_Retries_After_A_Faulted_Call()
    {
        // First attempt fails (auth), second succeeds: the fault must be evicted so the
        // retry re-issues the request rather than replaying the cached failure.
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.Unauthorized, """{"message":"TF400813: not authorized"}""")
            .Respond(HttpStatusCode.OK,
                """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");
        var api = Api(handler);

        await Assert.ThrowsAsync<AdoApiException>(() => api.GetIdentityAsync(TestContext.Current.CancellationToken));
        var user = await api.GetIdentityAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jin", user.DisplayName);
        Assert.Equal(2, handler.Requests.Count); // faulted attempt evicted, retry re-fetched
    }

    [Fact]
    public async Task GetIdentity_Retries_After_A_Canceled_Call()
    {
        // An HttpClient timeout surfaces as a *canceled* task (TaskCanceledException) even though
        // the caller never cancelled. That must be evicted like a fault so a later call retries,
        // not cached for the whole session.
        var handler = new FakeHttpHandler()
            .Respond(_ => throw new TaskCanceledException("simulated HttpClient timeout"))
            .Respond(HttpStatusCode.OK,
                """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");
        var api = Api(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetIdentityAsync(TestContext.Current.CancellationToken));
        var user = await api.GetIdentityAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jin", user.DisplayName);
        Assert.Equal(2, handler.Requests.Count); // canceled attempt evicted, retry re-fetched
    }

    [Fact]
    public async Task Prime_Swallows_An_Expected_Fault()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, """{"message":"TF400813"}""");
        var api = Api(handler);

        // Warm-up path: an auth fault here has no user-visible job and must not surface.
        await api.PrimeIdentityAsync();
    }

    [Fact]
    public async Task Prime_Then_GetIdentity_Serves_The_Primed_Result_Without_A_Second_Call()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"authenticatedUser":{"id":"1f2e3d4c-0000-1111-2222-333344445555","providerDisplayName":"Jin"}}""");
        var api = Api(handler);

        await api.PrimeIdentityAsync();
        var user = await api.GetIdentityAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jin", user.DisplayName);
        Assert.Single(handler.Requests); // prime + read share one connectionData call
    }

    /// <summary>Counts requests and gates the first response so concurrent callers overlap in flight.</summary>
    private sealed class GatedIdentityHandler(string json) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
