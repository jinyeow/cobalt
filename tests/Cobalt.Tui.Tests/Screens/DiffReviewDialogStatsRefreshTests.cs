using System.Drawing;
using Cobalt.Core.Models;
using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The diff dialog's background stats-refresh path, now marshalled through <see cref="IUiPost"/>
/// (M2). Proves the coalescing gate collapses a burst of StatsChanged events into a single queued
/// render, re-arms after the queue drains, and that a stats refresh repaints the chrome without
/// rebuilding the diff pane.
/// </summary>
public class DiffReviewDialogStatsRefreshTests
{
    private static readonly IApplication App = Application.Create();

    private sealed class FakeTextInput : ITextInput
    {
        public Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeDiffSource : IPrDiffSource
    {
        public IReadOnlyList<FileChange> Changes { get; init; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrThread>>([]);
        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src", "Contoso.Web");

    private static async Task<(DiffReviewDialog Detail, Dialog Dialog, RecordingUiPost Post)> BuiltDialog()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "src")] = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var post = new RecordingUiPost();
        var detail = new DiffReviewDialog(App, vm, new FakeTextInput(), _ => { }, post: post);
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        return (detail, dialog, post);
    }

    [Fact]
    public async Task A_Burst_Of_StatsChanged_Coalesces_Into_One_Posted_Render()
    {
        var (detail, _, post) = await BuiltDialog();
        post.Posted.Clear(); // ignore anything queued during Build/Load

        for (var i = 0; i < 5; i++)
        {
            detail.OnStatsChanged();
        }

        // The gate lets only the first through; the other four ride along with the queued refresh.
        Assert.Single(post.Posted);
    }

    [Fact]
    public async Task The_Gate_Re_Arms_After_The_Queue_Drains()
    {
        var (detail, _, post) = await BuiltDialog();
        post.Posted.Clear();

        detail.OnStatsChanged();
        Assert.Single(post.Posted);

        post.RunAll(); // runs RunQueuedStatsRefresh, which reopens the gate

        detail.OnStatsChanged();
        Assert.Single(post.Posted); // re-armed: a fresh event queues exactly one more render
    }

    [Fact]
    public async Task Stats_Refresh_Repaints_Chrome_Without_Rebuilding_The_Diff_Pane()
    {
        var (detail, _, post) = await BuiltDialog();
        var sourceBefore = detail.DiffPane.Source; // the bound diff rows

        post.Posted.Clear();
        detail.OnStatsChanged();
        post.RunAll();

        // A stats refresh renders chrome only (includeDiffPane: false), so the diff pane's bound
        // source is left untouched — same reference, no O(rows) re-tokenize.
        Assert.Same(sourceBefore, detail.DiffPane.Source);
    }
}
