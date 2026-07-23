using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.Tests.Tasks;

public class SingleFlightCacheTests
{
    [Fact]
    public async Task Single_Schedule_Publishes_Once_With_The_Scheduled_Key()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();

        await cache.ScheduleAsync(
            "a",
            (key, _) => Task.FromResult(42),
            (key, value) => published.Add((key, value)));

        Assert.Equal([("a", 42)], published);
    }

    [Fact]
    public async Task Rapid_Reschedule_Cancels_Previous_Fetches_And_Only_The_Newest_Publishes()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var tokens = new Dictionary<string, CancellationToken>();

        // A and B are held open on TCSes that honour their token; C completes immediately.
        var taskA = cache.ScheduleAsync("a", (key, ct) => Hold(key, ct, tokens), Publish(published));
        var taskB = cache.ScheduleAsync("b", (key, ct) => Hold(key, ct, tokens), Publish(published));
        var taskC = cache.ScheduleAsync("c", (key, ct) => Task.FromResult(3), Publish(published));

        await Task.WhenAll(taskA, taskB, taskC);

        Assert.Equal([("c", 3)], published);
        Assert.True(tokens["a"].IsCancellationRequested);
        Assert.True(tokens["b"].IsCancellationRequested);
    }

    [Fact]
    public async Task Stamp_Guard_Drops_A_Superseded_Fetch_That_Ignores_Its_Token_And_Completes()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var b = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // B's fake IGNORES its token: cancellation alone cannot stop it from completing.
        var taskB = cache.ScheduleAsync("b", (_, _) => b.Task, Publish(published));
        var taskC = cache.ScheduleAsync("c", (_, _) => Task.FromResult(3), Publish(published));
        await taskC;

        // B completes successfully AFTER C was scheduled — only the stamp guard can drop it.
        b.SetResult(2);
        await taskB;

        Assert.Equal([("c", 3)], published);
    }

    [Fact]
    public async Task Superseded_Fetch_That_Faults_Is_Consumed_Without_Throwing()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var b = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var taskB = cache.ScheduleAsync("b", (_, _) => b.Task, Publish(published));
        var taskC = cache.ScheduleAsync("c", (_, _) => Task.FromResult(3), Publish(published));
        await taskC;

        // B faults AFTER being superseded: nobody is waiting on the abandoned fetch, so its
        // fault must be observed and swallowed rather than escaping (ADR 0013).
        b.SetException(new InvalidOperationException("boom"));
        await taskB;

        Assert.Equal([("c", 3)], published);
    }

    [Fact]
    public async Task Newest_Fetch_That_Faults_Propagates_Out_Of_Its_Returned_Task()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);

        var task = cache.ScheduleAsync(
            "a",
            (_, _) => Task.FromException<int>(new InvalidOperationException("boom")),
            (_, _) => { });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Lifetime_Cancellation_Completes_The_Newest_Fetch_Without_Throwing_And_Without_Publishing()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var cache = new SingleFlightCache<string, int>(lifetime.Token);
        var published = new List<(string Key, int Value)>();
        var tokens = new Dictionary<string, CancellationToken>();

        var task = cache.ScheduleAsync("a", (key, ct) => Hold(key, ct, tokens), Publish(published));
        await lifetime.CancelAsync();
        await task;

        Assert.Empty(published);
    }

    [Fact]
    public async Task Stamp_Increments_Once_Per_Schedule()
    {
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var before = cache.Stamp;

        await cache.ScheduleAsync("a", (_, _) => Task.FromResult(1), (_, _) => { });
        Assert.Equal(before + 1, cache.Stamp);

        await cache.ScheduleAsync("b", (_, _) => Task.FromResult(2), (_, _) => { });
        Assert.Equal(before + 2, cache.Stamp);
    }

    [Fact]
    public async Task Dispose_Cancels_The_InFlight_Fetch_And_A_Later_Schedule_Throws()
    {
        var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var tokens = new Dictionary<string, CancellationToken>();

        var task = cache.ScheduleAsync("a", (key, ct) => Hold(key, ct, tokens), Publish(published));
        cache.Dispose();
        await task;

        Assert.True(tokens["a"].IsCancellationRequested);
        Assert.Empty(published);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => cache.ScheduleAsync("b", (_, _) => Task.FromResult(2), Publish(published)));

        cache.Dispose(); // double-dispose is a no-op
    }

    [Fact]
    public async Task Dispose_Supersedes_A_Token_Ignoring_Fetch_So_Its_Late_Result_Never_Publishes()
    {
        var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var a = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The fake IGNORES its token: only the disposed-guard can stop the late publish.
        var task = cache.ScheduleAsync("a", (_, _) => a.Task, Publish(published));
        cache.Dispose();
        a.SetResult(1);
        await task;

        Assert.Empty(published);
    }

    [Fact]
    public async Task Dispose_Supersedes_A_Token_Ignoring_Fetch_So_Its_Late_Fault_Is_Consumed()
    {
        var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var published = new List<(string Key, int Value)>();
        var a = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = cache.ScheduleAsync("a", (_, _) => a.Task, Publish(published));
        cache.Dispose();

        // The fetch faults AFTER dispose: nobody is listening any more, so the fault must be
        // observed and swallowed (dispose is the final supersede), not thrown to the awaiter.
        a.SetException(new InvalidOperationException("boom"));
        await task;

        Assert.Empty(published);
    }

    private sealed record Snapshot(string Key, int Value);

    [Fact]
    public async Task Composition_Rapid_A_B_C_Leaves_Only_C_In_The_Published_State()
    {
        // The ticket's acceptance (#46): a fake source of controllable latency, moving A->B->C
        // fast, publishing snapshots into a Published<T> — only C lands, A/B are cancelled, and
        // no fault goes unobserved (every returned task completes cleanly).
        using var cache = new SingleFlightCache<string, int>(TestContext.Current.CancellationToken);
        var state = new Published<Snapshot>();
        var tokens = new Dictionary<string, CancellationToken>();
        void PublishSnapshot(string key, int value) => state.Publish(new Snapshot(key, value));

        var taskA = cache.ScheduleAsync("a", (key, ct) => Hold(key, ct, tokens), PublishSnapshot);
        var taskB = cache.ScheduleAsync("b", (key, ct) => Hold(key, ct, tokens), PublishSnapshot);
        var taskC = cache.ScheduleAsync("c", (_, _) => Task.FromResult(3), PublishSnapshot);

        await Task.WhenAll(taskA, taskB, taskC);

        var current = state.Current;
        Assert.NotNull(current);
        Assert.Equal(new Snapshot("c", 3), current);
        Assert.True(tokens["a"].IsCancellationRequested);
        Assert.True(tokens["b"].IsCancellationRequested);
    }

    /// <summary>A fetch held open forever that honours its token — completes only by cancellation.</summary>
    private static Task<int> Hold(string key, CancellationToken ct, Dictionary<string, CancellationToken> tokens)
    {
        tokens[key] = ct;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private static Action<string, int> Publish(List<(string Key, int Value)> published) =>
        (key, value) => published.Add((key, value));
}
