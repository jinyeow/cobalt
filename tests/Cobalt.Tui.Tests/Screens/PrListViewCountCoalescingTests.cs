using System.Drawing;
using Cobalt.Core.Models;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The PR list's comment-count coalescing, now marshalled through <see cref="Cobalt.Tui.App.IUiPost"/>
/// (M2). A burst of per-PR count arrivals collapses into a single queued render; draining it repaints
/// the badges; and a count that lands after the drain re-arms the gate and queues a fresh render.
/// </summary>
public class PrListViewCountCoalescingTests
{
    private static PullRequest Pr(int id) =>
        new(id, $"pr {id}", null, "active", false, "feature", "main", "succeeded", "Jin", "r1", "web", [], [], "abc");

    private sealed class FakePrSource(IReadOnlyList<PullRequest> items) : IPullRequestSource
    {
        public Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(PrListFilter filter, CancellationToken ct) =>
            Task.FromResult(items);
    }

    private static async Task WaitForAllCountsAsync(PrCommentCountEnricher enricher, IReadOnlyList<PullRequest> rows)
    {
        for (var i = 0; i < 200; i++)
        {
            if (rows.All(pr => enricher.TryGet(pr) is not null))
            {
                return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("comment counts never all landed");
    }

    /// <summary>
    /// Builds a laid-out PR list whose enrichment fetches all block on <paramref name="release"/>, so
    /// no count lands until the test opens the gate — making the pre-drain state deterministic.
    /// </summary>
    private static (PrListView View, PrCommentCountEnricher Enricher, IReadOnlyList<PullRequest> Rows)
        Build(RecordingUiPost post, int count, TaskCompletionSource release)
    {
        var items = Enumerable.Range(1, count).Select(Pr).ToList();
        var vm = new PrListViewModel(new FakePrSource(items));
        vm.LoadAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        // Each fetch parks on the gate, then returns a positive count so the badge (" 💬 N") shows.
        var enricher = new PrCommentCountEnricher(async (pr, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return pr.PullRequestId;
        });
        var view = new PrListView(post, vm, enricher);
        var window = new Window();
        window.Add(view);
        window.Layout(new Size(120, 20)); // wide + tall enough that all rows are visible and badges fit
        view.Render(); // format at real width while counts are still parked → no badges yet
        return (view, enricher, vm.Rows);
    }

    [Fact]
    public async Task A_Burst_Of_Counts_Coalesces_Into_One_Render_That_Paints_Every_Badge()
    {
        var post = new RecordingUiPost();
        var release = new TaskCompletionSource();
        var (view, enricher, rows) = Build(post, 5, release);

        // Nothing has landed yet: no render posted, no badge painted.
        Assert.Empty(post.Posted);
        Assert.DoesNotContain("💬", view.RowText(0));

        release.SetResult(); // all five counts land in a burst
        await WaitForAllCountsAsync(enricher, rows);

        // The coalescing gate queued exactly one render for the whole burst (post stays undrained).
        Assert.Single(post.Posted);
        Assert.DoesNotContain("💬", view.RowText(0)); // still not painted — the render is only queued

        post.RunAll();

        // Draining runs the single render, which reads every cached count → every row badged.
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Contains("💬", view.RowText(i));
        }

        view.Dispose();
    }

    [Fact]
    public async Task A_Count_Landing_After_The_Drain_Re_Arms_And_Posts_A_New_Render()
    {
        var post = new RecordingUiPost();
        var release = new TaskCompletionSource();
        var (view, enricher, rows) = Build(post, 3, release);

        release.SetResult();
        await WaitForAllCountsAsync(enricher, rows);
        post.RunAll(); // drain the coalesced render → the gate re-arms
        Assert.Empty(post.Posted);

        // A fresh count arrival (refetch after invalidation) must queue a new render, not be swallowed.
        enricher.Invalidate();
        await enricher.EnrichAsync([rows[0]], TestContext.Current.CancellationToken);

        Assert.Single(post.Posted);

        view.Dispose();
    }
}
