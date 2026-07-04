using System.Net;
using Cobalt.Core.Auth;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Auth;

public class BearerTokenHandlerTests
{
    private sealed class FakeTokenProvider(string token) : ITokenProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(token);
        }
    }

    [Fact]
    public async Task Attaches_Bearer_Token_To_Every_Request()
    {
        var inner = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, "{}")
            .Respond(HttpStatusCode.OK, "{}");
        var provider = new FakeTokenProvider("tok-123");
        using var client = new HttpClient(new BearerTokenHandler(provider) { InnerHandler = inner });

        await client.GetAsync(new Uri("https://dev.azure.com/x/_apis/a"), TestContext.Current.CancellationToken);
        await client.GetAsync(new Uri("https://dev.azure.com/x/_apis/b"), TestContext.Current.CancellationToken);

        Assert.All(inner.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal("tok-123", r.Headers.Authorization?.Parameter);
        });
        Assert.Equal(2, provider.Calls);
    }
}
