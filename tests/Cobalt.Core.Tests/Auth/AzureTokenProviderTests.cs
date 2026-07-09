using Azure.Core;
using Azure.Identity;
using Cobalt.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Cobalt.Core.Tests.Auth;

public class AzureTokenProviderTests
{
    // ---- guards for the credential-chain fix (interactive-record first, az second) ----

    [Fact]
    public void Interactive_NeedsInteraction_Is_A_CredentialUnavailable_So_The_Chain_Falls_Through()
    {
        // With the browser credential first and `DisableAutomaticAuthentication`, a request that
        // can't be satisfied silently throws AuthenticationRequiredException. ChainedTokenCredential
        // only continues to the next credential (az) on CredentialUnavailableException — so this MUST
        // be one, or the chain would halt before ever trying az.
        Assert.True(typeof(CredentialUnavailableException)
            .IsAssignableFrom(typeof(AuthenticationRequiredException)));
    }

    [Fact]
    public void AzureCli_Process_Timeout_Is_Configurable()
    {
        // The fix raises az's process timeout so a slow-but-working `az` doesn't hard-time-out
        // (a non-CredentialUnavailable failure that halts the chain). Guard the option exists.
        Assert.NotNull(typeof(AzureCliCredentialOptions).GetProperty(nameof(AzureCliCredentialOptions.ProcessTimeout)));
    }

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
