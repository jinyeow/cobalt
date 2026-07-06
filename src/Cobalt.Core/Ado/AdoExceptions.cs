namespace Cobalt.Core.Ado;

/// <summary>
/// The expected, user-surfaceable failure set for an Azure DevOps operation: an API
/// error, a transport/JSON fault, an auth failure, or local IO. View-models convert
/// these to their <c>Error</c>/message-bar state; anything outside the set is a bug
/// and propagates to the global crash boundary (ADR 0013). Mirrors the whitelist in
/// <c>CobaltTuiApp</c>/<c>AuthCommands</c>.
///
/// <para><see cref="OperationCanceledException"/> is deliberately excluded: callers
/// rethrow/propagate cancellation before consulting this predicate.</para>
/// </summary>
public static class AdoExceptions
{
    public static bool IsExpected(Exception ex) => ex is
        AdoApiException or
        HttpRequestException or
        System.Text.Json.JsonException or
        Azure.Identity.AuthenticationFailedException or
        System.IO.IOException;

    /// <summary>The friendly, user-surfaceable message for a request timeout.</summary>
    public const string TimeoutMessage = "request timed out — check your connection and try again";

    /// <summary>
    /// Distinguishes an <see cref="HttpClient"/> timeout from a genuine user cancellation.
    /// A user/dialog cancel throws an <see cref="OperationCanceledException"/> carrying the
    /// caller's own token; a <c>HttpClient</c> 100s timeout surfaces as a
    /// <see cref="TaskCanceledException"/> whose token is the client's internal timeout
    /// token, not ours. So an OCE whose token is <em>not</em> the caller's is a timeout —
    /// an expected error to surface in the bar, not a silent cancel to swallow.
    /// </summary>
    public static bool IsTimeout(OperationCanceledException ex, CancellationToken callerToken) =>
        ex.CancellationToken != callerToken;
}
