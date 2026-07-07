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

    [Fact]
    public async Task Cancellation_Between_Queue_And_Run_Skips_Suspend_And_Body()
    {
        // The dialog can close (cancelling its token) after the invoke is queued but
        // before it runs — we must not open an editor over the shell in that window.
        var log = new List<string>();
        Action? queued = null;
        using var cts = new CancellationTokenSource();
        var suspender = new UiThreadSuspender(
            a => queued = a, // store, do not run yet
            () => log.Add("suspend"),
            () => log.Add("resume"));

        var task = suspender.RunSuspendedAsync(() => { log.Add("body"); return 0; }, cts.Token);
        cts.Cancel();       // dialog closed while the invoke was queued
        queued!();          // now the UI thread runs it

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Empty(log);  // never suspended, never ran the editor
    }

    [Fact]
    public async Task Continuation_Does_Not_Run_Synchronously_On_The_Completing_Thread()
    {
        // Pins RunContinuationsAsynchronously: the awaiting continuation (in production,
        // the follow-up ADO call) must NOT execute inline on the thread that completes
        // the TCS — otherwise it would run on the parked UI loop. Deleting the flag
        // makes this fail; the happy-path tests would still pass, so this is the guard.
        Action? queued = null;
        var suspender = new UiThreadSuspender(a => queued = a, () => { }, () => { });

        var task = suspender.RunSuspendedAsync(() => 7, TestContext.Current.CancellationToken);

        var continuationThreadId = 0;
        using var continuationRan = new ManualResetEventSlim(false);
        // ExecuteSynchronously asks to run inline on the completing thread; the flag on
        // the TCS must override that and force the continuation onto another thread.
        var continuation = task.ContinueWith(
            _ =>
            {
                continuationThreadId = Environment.CurrentManagedThreadId;
                continuationRan.Set();
            },
            TestContext.Current.CancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var completingThreadId = Environment.CurrentManagedThreadId;
        queued!();                       // completes the TCS via TrySetResult(7)

        Assert.True(continuationRan.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await continuation;
        // The continuation ran on a different thread than the one that completed the TCS —
        // i.e. asynchronously, not inline. Remove RunContinuationsAsynchronously and it runs
        // inline on the completing thread, so the ids match and this fails. Reads the thread
        // the continuation actually ran on, so there is no timing race (the old flag-window
        // version could see the pool thread run before the flag was reset).
        Assert.NotEqual(completingThreadId, continuationThreadId);
        Assert.Equal(7, await task);
    }
}
