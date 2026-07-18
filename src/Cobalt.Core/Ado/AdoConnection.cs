using System.Net;
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
        Teams = new TeamsApi(Http);
    }

    public AdoContext Context { get; }
    public AdoHttp Http { get; }
    public IdentityApi Identity { get; }
    public TeamsApi Teams { get; }

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
                InnerHandler = new SocketsHttpHandler
                {
                    // The default 60s idle timeout drops the pooled connection while the reviewer
                    // reads a single diff, so the next keystroke re-pays the ~700ms cold
                    // DNS + TCP + TLS. Five minutes spans a realistic reading pause; the finite
                    // lifetime still recycles connections so DNS changes are eventually picked up
                    // (the default is infinite).
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    // Default is None, which sends no Accept-Encoding at all. Whether ADO
                    // compresses authenticated API responses is unverified — free either way.
                    AutomaticDecompression = DecompressionMethods.All,
                },
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
