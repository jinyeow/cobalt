using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.Tests.Tasks;

public class JoinFlightCacheTests
{
    [Fact]
    public async Task Two_Concurrent_Callers_On_One_Key_Join_A_Single_Fetch()
    {
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return gate.Task;
        }

        var first = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        var second = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        gate.SetResult(42);

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, Volatile.Read(ref starts)); // both callers joined one fetch
    }

    [Fact]
    public async Task A_Successful_Fetch_Is_Cached_For_Later_Callers()
    {
        var cache = new JoinFlightCache<string, object>();
        var starts = 0;
        var value = new object();

        Task<object> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult(value);
        }

        var first = await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        var second = await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);

        Assert.Same(value, first);
        Assert.Same(first, second); // one cached result served both calls
        Assert.Equal(1, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task Distinct_Keys_Fetch_Independently()
    {
        var cache = new JoinFlightCache<string, string>();
        var starts = new List<string>();

        Task<string> Start(string key)
        {
            lock (starts)
            {
                starts.Add(key);
            }
            return Task.FromResult(key.ToUpperInvariant());
        }

        Assert.Equal("A", await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal("B", await cache.GetOrJoinAsync("b", Start, TestContext.Current.CancellationToken));

        Assert.Equal(["a", "b"], starts);
    }

    [Fact]
    public async Task A_Fault_Reaches_Every_Joiner_And_Evicts_So_A_Retry_Is_Not_Poisoned()
    {
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return fail ? gate.Task : Task.FromResult(7);
        }

        var first = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        var second = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        gate.SetException(new InvalidOperationException("boom"));

        Assert.Equal("boom", (await Assert.ThrowsAsync<InvalidOperationException>(() => first)).Message);
        Assert.Equal("boom", (await Assert.ThrowsAsync<InvalidOperationException>(() => second)).Message);

        // The faulted entry must be gone, so the retry re-fetches instead of rethrowing forever.
        fail = false;
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task A_Canceled_Fetch_Reaches_Every_Joiner_And_Evicts_So_A_Retry_Is_Not_Poisoned()
    {
        // An HttpClient timeout surfaces the shared fetch as a *canceled* task on a token no
        // caller owns, not as a fault. If eviction only fired for faults it would be cached
        // forever and every later call would instantly rethrow with no network.
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var fail = true;

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return fail ? gate.Task : Task.FromResult(7);
        }

        var first = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        var second = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        gate.SetCanceled(foreign.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        fail = false;
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task An_Already_Completed_Unsuccessful_Fetch_Evicts_Inline_And_Is_Never_Cached()
    {
        // start() can hand back a task that has already ended unsuccessfully. The entry is stored
        // before the eviction attaches, so the synchronous continuation runs inline — while the
        // starting caller still holds the gate — and must leave nothing cached behind it.
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        var fail = true;

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return fail ? Task.FromCanceled<int>(foreign.Token) : Task.FromResult(7);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));

        fail = false;
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task A_Caller_Cancelling_Its_Own_Await_Leaves_The_Shared_Fetch_Running_For_The_Others()
    {
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return gate.Task;
        }

        using var quitter = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var leaving = cache.GetOrJoinAsync("a", Start, quitter.Token);
        var staying = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);

        // One caller walks away while the shared fetch is still gated in flight.
        await quitter.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        gate.SetResult(42);
        Assert.Equal(42, await staying); // the shared fetch was never cancelled with the quitter

        // The good entry survived a caller-cancel: a later call is served from the cache.
        Assert.Equal(42, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task A_Fault_With_No_Remaining_Awaiter_Still_Evicts_So_The_Next_Call_Refetches()
    {
        // The sole caller walks away first, so nobody is left to see the fault. Eviction hangs off
        // the shared task rather than any caller's await, so the poisoned entry is gone before the
        // next caller arrives — it re-fetches rather than paying one guaranteed failure first.
        // Scope note: this pins the eviction, NOT the `_ = fetch.Exception` observation beside it.
        // Asserting that needs TaskScheduler.UnobservedTaskException — a process-global hook that
        // would cross-talk with a parallel suite run — so the observation is argued in the
        // primitive's doc comment (ADR 0013) rather than pinned by a test that would still pass
        // with it deleted.
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return fail ? gate.Task : Task.FromResult(7);
        }

        using var quitter = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var only = cache.GetOrJoinAsync("a", Start, quitter.Token);
        await quitter.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);

        gate.SetException(new InvalidOperationException("boom")); // nobody is awaiting any more

        fail = false;
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public async Task A_Retry_After_A_Fault_Keeps_Its_Result_Cached()
    {
        // The consequence form of identity eviction: once a fault has evicted, the retry's result
        // must stay cached rather than be torn out from under the next caller.
        // Scope note: this does NOT construct the stale-continuation interleaving it protects
        // against, and would still pass if eviction removed by key alone. That ordering — a late
        // continuation running after a newer entry exists — is not deterministically schedulable
        // from outside, since a newer entry can only be installed once the old task completed.
        // Identity eviction is therefore structural: the ReferenceEquals guard in
        // EvictIfUnsuccessful is where it is enforced and reviewed, not proven here.
        var cache = new JoinFlightCache<string, int>();
        var starts = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;

        Task<int> Start(string _)
        {
            Interlocked.Increment(ref starts);
            return fail ? gate.Task : Task.FromResult(7);
        }

        var failing = cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken);
        gate.SetException(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);

        fail = false;
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));
        Assert.Equal(7, await cache.GetOrJoinAsync("a", Start, TestContext.Current.CancellationToken));

        Assert.Equal(2, Volatile.Read(ref starts)); // the replacement entry survived
    }
}
