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
}
