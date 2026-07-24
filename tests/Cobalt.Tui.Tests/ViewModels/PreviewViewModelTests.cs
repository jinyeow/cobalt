using System.Collections.Concurrent;
using Cobalt.Tui.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The preview pipeline's load invariant (#49, ADR 0024 §"The load invariant"), proven purely:
/// a fake detail source of controllable latency plus a fake <see cref="TimeProvider"/> so the
/// debounce is driven, never slept through. Each clause of the invariant is a test here.
/// </summary>
public class PreviewViewModelTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private static ItemKey Pr(int id) => new(AppSection.PullRequests, id, null);

    /// <summary>A detail source that answers instantly and records what it was asked for.</summary>
    private sealed class InstantSource
    {
        public ConcurrentQueue<ItemKey> Fetched { get; } = new();

        public Task<string> FetchAsync(ItemKey key, CancellationToken ct)
        {
            Fetched.Enqueue(key);
            return Task.FromResult(Detail(key));
        }
    }

    /// <summary>The detail text this fake answers for <paramref name="key"/> — keyed so a torn
    /// publish (one item's text under another's key) is detectable.</summary>
    private static string Detail(ItemKey key) => $"detail:{key.Id}";

    private static string Summary(ItemKey key) => $"summary:{key.Id}";

    /// <summary>A detail source of controllable latency: every fetch hangs until the test completes
    /// or faults it, and it honours its cancellation token the way a real ADO call does.</summary>
    private sealed class HeldSource
    {
        private readonly ConcurrentDictionary<ItemKey, TaskCompletionSource<string>> _pending = new();
        private readonly ConcurrentDictionary<ItemKey, TaskCompletionSource<bool>> _started = new();
        private int _inFlight;
        private int _peak;

        public ConcurrentDictionary<ItemKey, CancellationToken> Tokens { get; } = new();

        /// <summary>The most fetches this source ever had running at once.</summary>
        public int PeakInFlight => Volatile.Read(ref _peak);

        public async Task<string> FetchAsync(ItemKey key, CancellationToken ct)
        {
            Tokens[key] = ct;
            var pending = Pending(key);
            using var registration = ct.Register(() => pending.TrySetCanceled(ct));
            RecordEntry();
            Started(key).TrySetResult(true);
            try
            {
                return await pending.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        /// <summary>Completes once <paramref name="key"/>'s fetch has actually begun — the fetch runs
        /// on a debounce continuation, so tests wait for it instead of guessing.</summary>
        public Task WaitForFetchAsync(ItemKey key) => Started(key).Task;

        public void Complete(ItemKey key, string text) => Pending(key).TrySetResult(text);

        private void RecordEntry()
        {
            var live = Interlocked.Increment(ref _inFlight);
            int peak;
            while ((peak = Volatile.Read(ref _peak)) < live)
            {
                Interlocked.CompareExchange(ref _peak, live, peak);
            }
        }

        private TaskCompletionSource<string> Pending(ItemKey key) =>
            _pending.GetOrAdd(key, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource<bool> Started(ItemKey key) =>
            _started.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>A detail source that IGNORES its cancellation token: only the stamp guard can stop
    /// its late result or fault from landing (the trap ADR 0008 names).</summary>
    private sealed class TokenIgnoringSource
    {
        private readonly ConcurrentDictionary<ItemKey, TaskCompletionSource<string>> _pending = new();
        private readonly ConcurrentDictionary<ItemKey, TaskCompletionSource<bool>> _started = new();

        public Task<string> FetchAsync(ItemKey key, CancellationToken ct)
        {
            Started(key).TrySetResult(true);
            return Pending(key).Task;
        }

        public Task WaitForFetchAsync(ItemKey key) => Started(key).Task;

        public void Complete(ItemKey key, string text) => Pending(key).TrySetResult(text);

        public void Fault(ItemKey key, Exception error) => Pending(key).TrySetException(error);

        private TaskCompletionSource<string> Pending(ItemKey key) =>
            _pending.GetOrAdd(key, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

        private TaskCompletionSource<bool> Started(ItemKey key) =>
            _started.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Fact]
    public void Tier_One_Paints_From_Row_Data_Synchronously_And_Fetches_Nothing()
    {
        var source = new InstantSource();
        using var vm = new PreviewViewModel(
            source.FetchAsync, TestContext.Current.CancellationToken, new FakeTimeProvider(), Debounce);

        var pending = vm.ShowAsync(Pr(1), Summary(Pr(1)));

        // Painted before anything is awaited, and with zero fetches: tier 1 is row data only.
        var state = vm.Current;
        Assert.NotNull(state);
        Assert.Equal(Pr(1), state.Key);
        Assert.False(state.Detailed);
        Assert.Equal(Summary(Pr(1)), state.Text);
        Assert.Empty(source.Fetched);
        Assert.False(pending.IsCompleted); // tier 2 is still waiting on the debounce
    }

    [Fact]
    public async Task Tier_Two_Publishes_The_Fetched_Detail_Once_The_Cursor_Settles()
    {
        var source = new InstantSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var pending = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await pending;

        var state = vm.Current;
        Assert.NotNull(state);
        Assert.Equal(Pr(1), state.Key);
        Assert.True(state.Detailed);
        Assert.Equal(Detail(Pr(1)), state.Text);
        Assert.Equal([Pr(1)], source.Fetched);
    }

    [Fact]
    public async Task Holding_J_Enqueues_No_Fetch_Until_The_Cursor_Stops()
    {
        // Clause 4: cursor moves repaint tier 1 locally; nothing is enqueued while moving.
        var source = new InstantSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var pending = new List<Task>();
        for (var id = 1; id <= 10; id++)
        {
            pending.Add(vm.ShowAsync(Pr(id), Summary(Pr(id))));
            time.Advance(TimeSpan.FromMilliseconds(50)); // still moving: never a full debounce apart
            Assert.Empty(source.Fetched);
            Assert.Equal(Summary(Pr(id)), vm.Current!.Text);
        }

        time.Advance(Debounce); // the cursor settles on the last row
        await Task.WhenAll(pending);

        Assert.Equal([Pr(10)], source.Fetched);
        Assert.Equal(new PreviewState(Pr(10), true, Detail(Pr(10))), vm.Current);
    }

    [Fact]
    public async Task Moving_A_To_B_To_C_Fast_Publishes_Only_C_And_Never_Tears()
    {
        // The canonical case (ADR 0024): A->B->C fast, then settle. Only C fetches and publishes,
        // and every state the pane ever saw pairs its own key with its own text.
        var source = new InstantSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);
        var seen = new List<PreviewState>();
        vm.Changed += () => seen.Add(vm.Current!);

        var a = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        var b = vm.ShowAsync(Pr(2), Summary(Pr(2)));
        var c = vm.ShowAsync(Pr(3), Summary(Pr(3)));
        time.Advance(Debounce);
        await Task.WhenAll(a, b, c);

        Assert.Equal([Pr(3)], source.Fetched);
        Assert.Equal(new PreviewState(Pr(3), true, Detail(Pr(3))), vm.Current);
        // Clause 1: no torn state — a key never carried another item's text, at any point.
        Assert.All(seen, s => Assert.Equal(s.Detailed ? Detail(s.Key) : Summary(s.Key), s.Text));
        Assert.Equal([Pr(1), Pr(2), Pr(3), Pr(3)], seen.Select(s => s.Key));
    }

    [Fact]
    public async Task Superseding_Cancels_The_InFlight_Fetch_Before_The_Next_One_Starts()
    {
        // Clause 3: one in-flight fetch; a new key cancels the previous CTS. The fake honours its
        // token like a real ADO call, so the superseded fetch is gone before the next one begins.
        var source = new HeldSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var a = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(1)); // A's fetch is genuinely running

        var b = vm.ShowAsync(Pr(2), Summary(Pr(2)));
        await a; // the superseded schedule completes — cancelled, not faulted

        Assert.True(source.Tokens[Pr(1)].IsCancellationRequested);
        Assert.False(a.IsFaulted);
        // The pane fell back to B's row data; A's detail never landed.
        Assert.Equal(new PreviewState(Pr(2), false, Summary(Pr(2))), vm.Current);

        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(2));
        source.Complete(Pr(2), Detail(Pr(2)));
        await b;

        Assert.Equal(new PreviewState(Pr(2), true, Detail(Pr(2))), vm.Current);
        Assert.Equal(1, source.PeakInFlight);
    }

    [Fact]
    public async Task A_Stale_Completion_That_Ignores_Its_Token_Is_Dropped_Whole()
    {
        // Clause 2: cancellation is cooperative, so a fetch may ignore its token and still complete.
        // The monotonic stamp — not the cancel — is what keeps its result off the pane.
        var source = new TokenIgnoringSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var a = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(1));

        var b = vm.ShowAsync(Pr(2), Summary(Pr(2)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(2));
        source.Complete(Pr(2), Detail(Pr(2)));
        await b;

        source.Complete(Pr(1), Detail(Pr(1))); // A answers late, ignoring its cancelled token
        await a;

        Assert.Equal(new PreviewState(Pr(2), true, Detail(Pr(2))), vm.Current);
    }

    [Fact]
    public async Task A_Superseded_Fetch_That_Faults_Never_Escapes_To_The_Crash_Log()
    {
        // Clause 3: the abandoned fetch's fault is observed here, so it can never resurface as an
        // unobserved task exception in the crash-log hook (ADR 0013).
        var source = new TokenIgnoringSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var a = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(1));

        var b = vm.ShowAsync(Pr(2), Summary(Pr(2)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(2));
        source.Complete(Pr(2), Detail(Pr(2)));
        await b;

        source.Fault(Pr(1), new InvalidOperationException("boom"));
        await a; // must not throw

        Assert.Equal(new PreviewState(Pr(2), true, Detail(Pr(2))), vm.Current);
    }

    [Fact]
    public async Task Re_Showing_The_Item_Already_On_Screen_Neither_Repaints_Nor_Refetches()
    {
        // The shell calls Show on every cursor move, list re-render and layout pass; only a real
        // change of the displayed item may restart the pipeline.
        var source = new InstantSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var first = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await first;
        var changes = 0;
        vm.Changed += () => changes++;

        await vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);

        Assert.Equal(0, changes);
        Assert.Equal([Pr(1)], source.Fetched);
        Assert.True(vm.Current!.Detailed); // still the detail, not knocked back to tier 1
    }

    [Fact]
    public async Task Clearing_Empties_The_Pane_And_A_Late_Detail_Cannot_Repaint_It()
    {
        var source = new TokenIgnoringSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var pending = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        time.Advance(Debounce);
        await source.WaitForFetchAsync(Pr(1));

        vm.Clear();
        source.Complete(Pr(1), Detail(Pr(1))); // the fetch for a no-longer-displayed item lands
        await pending;

        Assert.Null(vm.Current);
    }

    [Fact]
    public async Task Clearing_Cancels_A_Pending_Debounce_So_No_Fetch_Fires()
    {
        // Collapse/deselect must not merely drop a completion — it must stop the fetch from being
        // spent at all (ADR 0024: a hidden pane spends no round-trips).
        var source = new InstantSource();
        var time = new FakeTimeProvider();
        using var vm = new PreviewViewModel(source.FetchAsync, TestContext.Current.CancellationToken, time, Debounce);

        var pending = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        vm.Clear();
        time.Advance(Debounce); // the settle that would have fired the fetch
        await pending;

        Assert.Empty(source.Fetched);
        Assert.Null(vm.Current);
    }

    [Fact]
    public async Task After_Dispose_Show_Is_A_Safe_No_Op()
    {
        // A UI-thread post queued before teardown can still run after Dispose; it must not fault
        // the pipeline (that fault would reach the crash-log hook via FireAndForget).
        var vm = new PreviewViewModel(
            new InstantSource().FetchAsync, TestContext.Current.CancellationToken, new FakeTimeProvider(), Debounce);
        vm.Dispose();

        await vm.ShowAsync(Pr(1), Summary(Pr(1))); // must not throw
        vm.Clear();

        Assert.Null(vm.Current);
    }

    [Fact]
    public async Task Workspace_Shutdown_Cancels_The_Pending_Fetch_Without_Publishing()
    {
        var source = new HeldSource();
        var time = new FakeTimeProvider();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var vm = new PreviewViewModel(source.FetchAsync, lifetime.Token, time, Debounce);

        var pending = vm.ShowAsync(Pr(1), Summary(Pr(1)));
        await lifetime.CancelAsync(); // the shell is going away while the debounce is still ticking
        time.Advance(Debounce);
        await pending;

        Assert.Empty(source.Tokens); // the fetch was never reached
        Assert.Equal(new PreviewState(Pr(1), false, Summary(Pr(1))), vm.Current);
    }
}
