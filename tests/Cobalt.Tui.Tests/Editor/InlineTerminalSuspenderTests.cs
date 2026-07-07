using Cobalt.Tui.Editor;

namespace Cobalt.Tui.Tests.Editor;

public class InlineTerminalSuspenderTests
{
    [Fact]
    public async Task Runs_Body_And_Returns_Its_Value()
    {
        var suspender = new InlineTerminalSuspender();

        var result = await suspender.RunSuspendedAsync(() => 7, TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task Body_Exception_Faults_The_Task()
    {
        var suspender = new InlineTerminalSuspender();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => suspender.RunSuspendedAsync(
                () => throw new InvalidOperationException("boom"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Precanceled_Token_Throws_And_Body_Never_Runs()
    {
        var suspender = new InlineTerminalSuspender();
        var ran = false;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => suspender.RunSuspendedAsync(() => { ran = true; return 0; }, cts.Token));

        Assert.False(ran);
    }
}
