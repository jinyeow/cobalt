using Cobalt.Core.Config;

namespace Cobalt.Tui.App;

/// <summary>
/// Runs a discarded ("fire-and-forget") UI action safely (ADR 0013). A user
/// cancellation (dialog closed / section switched) is silently ignored, but any
/// <em>unexpected</em> fault is both recorded to the crash log and surfaced to the
/// user immediately — instead of vanishing into the discarded task, where it would
/// only reappear later as a phantom <see cref="TaskScheduler.UnobservedTaskException"/>
/// with no message bar and no crash screen.
/// </summary>
public static class FireAndForget
{
    /// <summary>Production entry point: routes faults to the crash log and reports via <paramref name="report"/> on the UI thread.</summary>
    public static Task Observe(Task task, IUiPost post, Action<string> report) =>
        Observe(
            task,
            report,
            ex => CobaltTuiApp.LogBackgroundFault(ex, ConfigPaths.CrashLogFile(), DateTimeOffset.Now),
            post.Post);

    /// <summary>Testable core: the crash-log sink and UI-thread marshaller are injected.</summary>
    internal static async Task Observe(Task task, Action<string> report, Action<Exception> record, Action<Action> post)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User/dialog cancellation is never an error (mirrors IgnoreCancellationAsync).
        }
        catch (Exception ex)
        {
            // A genuine bug in a discarded action: record it AND tell the user right away,
            // rather than letting it disappear into an unobserved task (M3 / ADR 0013).
            record(ex);
            post(() => report($"unexpected error: {ex.Message}"));
        }
    }
}
