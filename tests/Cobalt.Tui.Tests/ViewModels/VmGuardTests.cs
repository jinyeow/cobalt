using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class VmGuardTests
{
    [Fact]
    public async Task Real_Cancel_Rethrows_And_Stays_Silent()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        string? reported = null;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            VmGuard.RunAsync(
                () => throw new OperationCanceledException(cts.Token),
                cts.Token,
                m => reported = m));

        Assert.Null(reported);
    }

    [Fact]
    public async Task Timeout_Cancel_Reports_TimeoutMessage()
    {
        using var callerCts = new CancellationTokenSource();
        using var foreignCts = new CancellationTokenSource();
        await foreignCts.CancelAsync();
        string? reported = null;

        await VmGuard.RunAsync(
            () => throw new TaskCanceledException("t", null, foreignCts.Token),
            callerCts.Token,
            m => reported = m);

        Assert.Equal(Cobalt.Core.Ado.AdoExceptions.TimeoutMessage, reported);
    }

    [Fact]
    public async Task Expected_Ado_Failure_Reports_Its_Message()
    {
        string? reported = null;

        await VmGuard.RunAsync(
            () => throw new HttpRequestException("boom"),
            CancellationToken.None,
            m => reported = m);

        Assert.Equal("boom", reported);
    }

    [Fact]
    public async Task Unexpected_Exception_Propagates_To_The_Crash_Boundary()
    {
        string? reported = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VmGuard.RunAsync(
                () => throw new InvalidOperationException("bug"),
                CancellationToken.None,
                m => reported = m));

        Assert.Null(reported);
    }

    [Fact]
    public async Task Value_Overload_Returns_Body_Result_On_Success()
    {
        var result = await VmGuard.RunAsync(
            () => Task.FromResult(42),
            CancellationToken.None,
            static _ => { });

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Value_Overload_Returns_Default_On_Expected_Failure()
    {
        string? reported = null;

        var result = await VmGuard.RunAsync<string?>(
            () => throw new HttpRequestException("boom"),
            CancellationToken.None,
            m => reported = m);

        Assert.Null(result);
        Assert.Equal("boom", reported);
    }

    [Fact]
    public async Task Success_Never_Reports()
    {
        var reported = false;

        await VmGuard.RunAsync(
            () => Task.CompletedTask,
            CancellationToken.None,
            _ => reported = true);

        Assert.False(reported);
    }
}
