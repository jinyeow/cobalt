using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.Tests.Tasks;

public class TaskCancellationExtensionsTests
{
    [Fact]
    public async Task Completes_Normally_When_Task_Is_Canceled()
    {
        using var cts = new CancellationTokenSource();
        var task = Task.Delay(Timeout.Infinite, cts.Token);
        await cts.CancelAsync();

        // Canceled task must not throw through IgnoreCancellationAsync.
        await task.IgnoreCancellationAsync();
    }

    [Fact]
    public async Task Rethrows_Other_Exceptions()
    {
        var task = Task.FromException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.IgnoreCancellationAsync());
    }

    [Fact]
    public async Task Completes_When_Task_Succeeds()
    {
        var completed = false;
        var task = Task.Run(() => completed = true, TestContext.Current.CancellationToken);

        await task.IgnoreCancellationAsync();

        Assert.True(completed);
    }
}
