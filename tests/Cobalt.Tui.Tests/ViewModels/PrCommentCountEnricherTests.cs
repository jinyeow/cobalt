using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrCommentCountEnricherTests
{
    private static PullRequest Pr(int id, string commit = "abc") =>
        new(id, $"pr {id}", null, "active", false, "feature", "main", "succeeded", "Jin", "r1", "web", [], [], commit);

    [Fact]
    public async Task Fetches_Only_Requested_Ids()
    {
        var fetched = new List<int>();
        var enricher = new PrCommentCountEnricher((pr, _) =>
        {
            lock (fetched) { fetched.Add(pr.PullRequestId); }
            return Task.FromResult(pr.PullRequestId);
        });

        await enricher.EnrichAsync([Pr(1), Pr(2)], TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], fetched.Order());
    }

    [Fact]
    public async Task Caches_And_Does_Not_Refetch_Same_Key()
    {
        var calls = 0;
        var enricher = new PrCommentCountEnricher((pr, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(7);
        });

        await enricher.EnrichAsync([Pr(1), Pr(2)], TestContext.Current.CancellationToken);
        await enricher.EnrichAsync([Pr(1), Pr(2)], TestContext.Current.CancellationToken);

        Assert.Equal(2, calls); // once per id, second pass served from cache
        Assert.Equal(7, enricher.TryGet(Pr(1)));
    }

    [Fact]
    public async Task Raises_CountAvailable_When_A_Count_Lands()
    {
        var raised = new List<int>();
        var enricher = new PrCommentCountEnricher((pr, _) => Task.FromResult(3));
        enricher.CountAvailable += id => { lock (raised) { raised.Add(id); } };

        await enricher.EnrichAsync([Pr(42)], TestContext.Current.CancellationToken);

        Assert.Contains(42, raised);
        Assert.Equal(3, enricher.TryGet(Pr(42)));
    }

    [Fact]
    public async Task Respects_Concurrency_Cap()
    {
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var enricher = new PrCommentCountEnricher(
            async (pr, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await gate.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref running);
                return 0;
            },
            maxConcurrency: 2);

        var rows = Enumerable.Range(1, 6).Select(i => Pr(i)).ToList();
        var task = enricher.EnrichAsync(rows, TestContext.Current.CancellationToken);

        // Wait until the cap is saturated, then confirm it never exceeds it.
        await WaitUntil(() => Volatile.Read(ref running) == 2);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref running));

        gate.SetResult();
        await task;
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Cancellation_Stops_Pending_Work()
    {
        var raised = new List<int>();
        using var cts = new CancellationTokenSource();
        var enricher = new PrCommentCountEnricher(async (pr, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        });
        enricher.CountAvailable += id => { lock (raised) { raised.Add(id); } };

        var rows = Enumerable.Range(1, 3).Select(i => Pr(i)).ToList();
        var task = enricher.EnrichAsync(rows, cts.Token);
        await cts.CancelAsync();
        await task; // completes: cancellation is swallowed, never surfaced

        Assert.Empty(raised);
        Assert.Null(enricher.TryGet(Pr(1)));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
