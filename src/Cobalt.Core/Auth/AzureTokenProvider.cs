using Azure.Core;
using Azure.Identity;

namespace Cobalt.Core.Auth;

/// <summary>
/// Entra ID tokens for Azure DevOps (ADR 0003): reuse `az login` when possible,
/// fall back to interactive browser sign-in with a persistent MSAL cache, and keep
/// the resulting <see cref="AuthenticationRecord"/> on disk so later runs stay silent.
/// </summary>
public sealed class AzureTokenProvider : ITokenProvider
{
    /// <summary>The Azure DevOps resource, requesting all its statically-declared scopes.</summary>
    public const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken _cached;

    public AzureTokenProvider(TokenCredential credential, TimeProvider? time = null)
    {
        _credential = credential;
        _time = time ?? TimeProvider.System;
    }

    public static AzureTokenProvider CreateDefault(string authRecordPath) =>
        new(BuildChain(authRecordPath));

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (StillFresh())
        {
            return _cached.Token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillFresh())
            {
                _cached = await _credential.GetTokenAsync(
                    new TokenRequestContext([AdoScope]), cancellationToken).ConfigureAwait(false);
            }
            return _cached.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool StillFresh() =>
        _cached.Token is { Length: > 0 } &&
        _cached.ExpiresOn - _time.GetUtcNow() > RefreshSkew;

    private static TokenCredential BuildChain(string authRecordPath)
    {
        return new ChainedTokenCredential(
            new AzureCliCredential(),
            new InteractiveBrowserCredential(BrowserOptions(authRecordPath)));
    }

    private static InteractiveBrowserCredentialOptions BrowserOptions(string authRecordPath)
    {
        var options = new InteractiveBrowserCredentialOptions
        {
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
        await using var stream = File.Create(authRecordPath);
        await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
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
