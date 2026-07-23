using Cobalt.Core.Ado;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// The one copy of the view-model timeout-vs-cancel classification (ADR 0013):
/// a genuine user/dialog cancel (OCE carrying the caller's token) rethrows silently;
/// an HttpClient timeout (OCE carrying a foreign token) reports AdoExceptions.TimeoutMessage;
/// an expected ADO failure reports ex.Message; anything else propagates to the crash boundary.
/// </summary>
internal static class VmGuard
{
    public static async Task RunAsync(Func<Task> body, CancellationToken ct, Action<string> onError)
    {
        try { await body().ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct)) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        { onError(ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message); }
    }

    public static async Task<T?> RunAsync<T>(Func<Task<T>> body, CancellationToken ct, Action<string> onError)
    {
        try { return await body().ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct)) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        { onError(ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message); return default; }
    }
}
