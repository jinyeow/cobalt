using Azure.Core;
using Cobalt.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Cobalt.Core.Tests.Auth;

public class AzureTokenProviderTests
{
    private sealed class CountingCredential : TokenCredential
    {
        private readonly SemaphoreSlim _block = new(0);
        private int _calls;

        public int Calls => _calls;
        public DateTimeOffset Expires { get; set; }
        public bool Block { get; set; }
        public CancellationToken? SeenToken { get; private set; }

        public void Unblock(int count) => _block.Release(count);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            SeenToken = cancellationToken;
            if (Block)
            {
                await _block.WaitAsync(cancellationToken);
            }
            return new AccessToken($"tok-{call}", Expires);
        }
    }

    [Fact]
    public async Task Caches_Token_While_Fresh()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential { Expires = time.GetUtcNow().AddHours(1) };
        var provider = new AzureTokenProvider(credential, time);

        var a = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        var b = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tok-1", a);
        Assert.Equal("tok-1", b);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task Refreshes_Inside_Five_Minute_Skew_Before_Expiry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential { Expires = time.GetUtcNow().AddMinutes(10) };
        var provider = new AzureTokenProvider(credential, time);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(6)); // 4 min left < 5 min skew, though not expired
        credential.Expires = time.GetUtcNow().AddHours(1);
        var refreshed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, credential.Calls);
        Assert.Equal("tok-2", refreshed);
    }

    [Fact]
    public async Task Concurrent_Cold_Calls_Fetch_Once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential { Expires = time.GetUtcNow().AddHours(1), Block = true };
        var provider = new AzureTokenProvider(credential, time);

        var first = provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        var second = provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        credential.Unblock(2); // if both raced past the gate, both would proceed
        var tokens = await Task.WhenAll(first, second);

        Assert.Equal(1, credential.Calls);
        Assert.All(tokens, t => Assert.Equal("tok-1", t));
    }

    [Fact]
    public async Task Cancellation_Propagates_To_Credential()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential { Expires = time.GetUtcNow().AddHours(1) };
        var provider = new AzureTokenProvider(credential, time);
        using var cts = new CancellationTokenSource();

        await provider.GetAccessTokenAsync(cts.Token);

        Assert.Equal(cts.Token, credential.SeenToken);
    }
}
