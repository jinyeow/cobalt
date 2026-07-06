namespace Cobalt.Tui.Tasks;

/// <summary>
/// Helper for fire-and-forget UI loads: awaits a task and swallows only
/// cancellation, so a dialog closing mid-load doesn't need its own empty
/// catch block. Anything else propagates.
/// </summary>
public static class TaskCancellationExtensions
{
    public static async Task IgnoreCancellationAsync(this Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            return;
        }
    }
}
