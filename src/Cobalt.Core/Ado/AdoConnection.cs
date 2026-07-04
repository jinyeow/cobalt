using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Cobalt.Core.Ado;

/// <summary>Everything needed to talk to one org/project: transport + typed API surfaces.</summary>
public sealed class AdoConnection : IDisposable
{
    private readonly HttpClient _client;

    private AdoConnection(AdoContext context, HttpClient client)
    {
        Context = context;
        _client = client;
        Http = new AdoHttp(client);
        Identity = new IdentityApi(Http);
    }

    public AdoContext Context { get; }
    public AdoHttp Http { get; }
    public IdentityApi Identity { get; }

    public static AdoConnection Create(AdoContext context, ITokenProvider tokens)
    {
        // Pipeline (outermost first): retry -> bearer -> socket, so every retry
        // attempt re-reads the (cached) token instead of replaying a stale one.
        var retry = new ResilienceHandler(
            new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRetry(new HttpRetryStrategyOptions()) // 429/5xx/408 + Retry-After aware
                .Build())
        {
            InnerHandler = new BearerTokenHandler(tokens)
            {
                InnerHandler = new SocketsHttpHandler(),
            },
        };

        var client = new HttpClient(retry)
        {
            BaseAddress = new Uri($"{context.OrganizationUrl.AbsoluteUri.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(100),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("cobalt-tui");

        return new AdoConnection(context, client);
    }

    public void Dispose() => _client.Dispose();
}
