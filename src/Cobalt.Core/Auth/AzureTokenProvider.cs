using Azure.Core;
using Azure.Identity;

namespace Cobalt.Core.Auth;

/// <summary>
/// Entra ID tokens for Azure DevOps (ADR 0003): prefer cobalt's own persisted browser
/// sign-in (silent from the saved <see cref="AuthenticationRecord"/>), then fall back to
/// reusing an `az login` session. A slow or broken `az` therefore can't block a working
/// sign-in, and later runs stay silent.
/// </summary>
public sealed class AzureTokenProvider : ITokenProvider
{
    /// <summary>The Azure DevOps resource, requesting all its statically-declared scopes.</summary>
    public const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Reference type so the lock-free fast path reads it atomically (no torn struct read).
    private sealed record Cached(string Token, DateTimeOffset ExpiresOn);

    private Cached? _cached;

    public AzureTokenProvider(TokenCredential credential, TimeProvider? time = null)
    {
        _credential = credential;
        _time = time ?? TimeProvider.System;
    }

    public static AzureTokenProvider CreateDefault(string authRecordPath) =>
        new(BuildChain(authRecordPath));

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (StillFresh(_cached) is { } fast)
        {
            return fast;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (StillFresh(_cached) is { } refreshed)
            {
                return refreshed;
            }
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext([AdoScope]), cancellationToken).ConfigureAwait(false);
            _cached = new Cached(token.Token, token.ExpiresOn);
            return token.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? StillFresh(Cached? cached) =>
        cached is not null && cached.ExpiresOn - _time.GetUtcNow() > RefreshSkew
            ? cached.Token
            : null;

    private static TokenCredential BuildChain(string authRecordPath)
    {
        // Try cobalt's own persisted sign-in FIRST (silent from the saved auth record — the
        // browser credential has DisableAutomaticAuthentication, so it never prompts here), then
        // fall back to reusing an `az login` session. This ordering makes `cobalt auth login`
        // self-sufficient: an `az` that is slow or broken can no longer block a working sign-in,
        // because AzureCliCredential's *timeout* is a hard failure that HALTS ChainedTokenCredential
        // (it only falls through on CredentialUnavailableException). Users without a cobalt record
        // still get `az` reuse via the fall-through, and az's process timeout is raised so a
        // slow-but-working CLI on a locked-down/corporate machine doesn't time out.
        return new ChainedTokenCredential(
            new InteractiveBrowserCredential(BrowserOptions(authRecordPath)),
            new AzureCliCredential(new AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) }));
    }

    private static InteractiveBrowserCredentialOptions BrowserOptions(string authRecordPath)
    {
        var options = new InteractiveBrowserCredentialOptions
        {
            // Silent paths (auth status, the TUI) must never pop a browser; only
            // `cobalt auth login` (AuthenticateAsync) may prompt.
            DisableAutomaticAuthentication = true,
            // UnencryptedStorageAllowed: on Linux without a keyring (headless/WSL) the
            // encrypted cache is unavailable; fall back to a file like `az` does.
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "cobalt",
                UnsafeAllowUnencryptedStorage = true,
            },
        };
        var record = TryLoadRecord(authRecordPath);
        if (record is not null)
        {
            options.AuthenticationRecord = record;
        }
        return options;
    }

    /// <summary>Interactive sign-in that persists the account record for future silent runs.</summary>
    public static async Task<AuthenticationRecord> LoginAsync(
        string authRecordPath, CancellationToken cancellationToken = default)
    {
        var credential = new InteractiveBrowserCredential(BrowserOptions(authRecordPath));
        var record = await credential.AuthenticateAsync(
            new TokenRequestContext([AdoScope]), cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(authRecordPath)!);
        await using (var stream = File.Create(authRecordPath))
        {
            await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(authRecordPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return record;
    }

    private static AuthenticationRecord? TryLoadRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt record: fall back to a fresh interactive login
        }
    }
}
