using Cobalt.Tui.Editor;

namespace Cobalt.Tui.Tests.Editor;

public class UiThreadSuspenderTests
{
    [Fact]
    public async Task Runs_Suspend_Body_Resume_In_Order()
    {
        var log = new List<string>();
        var suspender = new UiThreadSuspender(
            a => a(),
            () => log.Add("suspend"),
            () => log.Add("resume"));

        var result = await suspender.RunSuspendedAsync(
            () => { log.Add("body"); return 42; },
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(["suspend", "body", "resume"], log);
    }

    [Fact]
    public async Task Body_Runs_Via_The_Invoke_Delegate()
    {
        var log = new List<string>();
        var invokeCount = 0;
        var suspender = new UiThreadSuspender(
            a => { invokeCount++; log.Add("invoke"); a(); },
            () => log.Add("suspend"),
            () => log.Add("resume"));

        await suspender.RunSuspendedAsync(() => { log.Add("body"); return 0; }, TestContext.Current.CancellationToken);

        Assert.Equal(1, invokeCount);
        Assert.Equal(0, log.IndexOf("invoke"));
        Assert.True(log.IndexOf("invoke") < log.IndexOf("suspend"));
    }

    [Fact]
    public async Task Resume_Fires_When_Body_Throws()
    {
        var log = new List<string>();
        var suspender = new UiThreadSuspender(
            a => a(),
            () => log.Add("suspend"),
            () => log.Add("resume"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => suspender.RunSuspendedAsync(
                () => throw new InvalidOperationException("body boom"),
                TestContext.Current.CancellationToken));

        Assert.Equal("resume", log[^1]);
    }

    [Fact]
    public async Task Resume_Fires_When_Suspend_Throws()
    {
        var log = new List<string>();
        var bodyRan = false;
        var suspender = new UiThreadSuspender(
            a => a(),
            () => { log.Add("suspend"); throw new InvalidOperationException("suspend boom"); },
            () => log.Add("resume"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => suspender.RunSuspendedAsync(
                () => { bodyRan = true; return 0; },
                TestContext.Current.CancellationToken));

        Assert.Equal("suspend boom", ex.Message);
        Assert.False(bodyRan);
        Assert.Contains("resume", log);
    }

    [Fact]
    public async Task Resume_Exception_Faults_The_Task()
    {
        var suspender = new UiThreadSuspender(
            a => a(),
            () => { },
            () => throw new InvalidOperationException("resume boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => suspender.RunSuspendedAsync(() => 0, TestContext.Current.CancellationToken));

        Assert.Equal("resume boom", ex.Message);
    }

    [Fact]
    public async Task Precanceled_Token_Skips_Suspend()
    {
        var log = new List<string>();
        var suspender = new UiThreadSuspender(
            a => a(),
            () => log.Add("suspend"),
            () => log.Add("resume"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => suspender.RunSuspendedAsync(() => { log.Add("body"); return 0; }, cts.Token));

        Assert.Empty(log);
    }

    [Fact]
    public async Task Completes_From_A_Queued_Invoke()
    {
        Action? queued = null;
        var suspender = new UiThreadSuspender(
            a => queued = a,   // store, do not run inline
            () => { },
            () => { });

        var task = suspender.RunSuspendedAsync(() => 5, TestContext.Current.CancellationToken);

        Assert.False(task.IsCompleted);   // nothing has run the queued action yet
        Assert.NotNull(queued);

        await Task.Run(() => queued!(), TestContext.Current.CancellationToken);

        Assert.Equal(5, await task);
    }
}
