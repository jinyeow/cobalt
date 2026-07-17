using System.Net;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

public sealed class IdentityApi(AdoHttp http)
{
    private readonly object _gate = new();
    private Task<AdoUser>? _identity;

    /// <summary>Resolves the signed-in user via connectionData (needed for @Me and reviewer votes).</summary>
    public async Task<AdoUser> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        var data = await http.GetJsonAsync(
            "_apis/connectionData?api-version=7.2-preview.1",
            AdoJsonContext.Default.ConnectionData,
            cancellationToken).ConfigureAwait(false);

        var user = data.AuthenticatedUser;
        if (user is null || user.Id == Guid.Empty)
        {
            throw new AdoApiException(
                HttpStatusCode.Unauthorized,
                "Azure DevOps did not identify the signed-in user (token may lack the Azure DevOps scope)");
        }

        return new AdoUser(
            user.Id,
            user.CustomDisplayName ?? user.ProviderDisplayName ?? user.Id.ToString(),
            user.Descriptor);
    }

    /// <summary>
    /// Reset-on-fault single-flight cache over <see cref="GetAuthenticatedUserAsync"/>: the
    /// signed-in user is resolved once and shared, so the status bar, @Me query and reviewer
    /// filters make a single <c>connectionData</c> call between them. Concurrent callers join
    /// the in-flight fetch instead of each issuing their own. A faulted attempt is evicted so a
    /// transient auth/network failure can be retried by a later caller.
    ///
    /// <para>The shared fetch is started detached from any one caller's token (started with
    /// <see cref="CancellationToken.None"/>) so a joiner is never bound to a cancelled starter;
    /// each caller observes its own <paramref name="cancellationToken"/> via <c>WaitAsync</c>
    /// (ADR 0008).</para>
    /// </summary>
    public Task<AdoUser> GetIdentityAsync(CancellationToken cancellationToken = default) =>
        GetOrStart().WaitAsync(cancellationToken);

    /// <summary>
    /// Warms the identity cache, swallowing expected faults (ADR 0013). Fire-and-forget after
    /// auth: it has no user-visible job, so an auth/network fault here must not reach the message
    /// bar or the crash log — the first real identity read hits the same fault and reports it with
    /// context. A faulted attempt is already evicted, so that later read still retries.
    /// </summary>
    public async Task PrimeIdentityAsync()
    {
        try
        {
            await GetIdentityAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or ObjectDisposedException || AdoExceptions.IsExpected(ex))
        {
        }
    }

    private Task<AdoUser> GetOrStart()
    {
        var existing = Volatile.Read(ref _identity);
        if (existing is not null)
        {
            return existing;
        }

        lock (_gate)
        {
            if (_identity is not null)
            {
                return _identity;
            }

            // Started detached (CancellationToken.None) and stored before wiring eviction, so a
            // synchronously-faulting fetch cannot null the field before it is set.
            var task = GetAuthenticatedUserAsync(CancellationToken.None);
            _identity = task;

            // Reset-on-fault-or-cancel: drop the cached task if the fetch faulted OR was cancelled
            // (an HttpClient timeout surfaces as a canceled task, not a faulted one) so the next
            // caller re-fetches. NotOnRanToCompletion fires for both. Only nulls the field if it is
            // still the one we stored — never evicts a newer in-flight attempt.
            _ = task.ContinueWith(
                t =>
                {
                    _ = t.Exception; // observe: awaiters still see it; a fetch nobody awaits does not crash-log
                    lock (_gate)
                    {
                        if (ReferenceEquals(_identity, t))
                        {
                            _identity = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return task;
        }
    }
}
