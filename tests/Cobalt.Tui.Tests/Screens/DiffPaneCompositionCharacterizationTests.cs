using System.Drawing;
using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tests.App;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// Characterization pins for the diff-pane composition embedded in
/// <see cref="DiffReviewDialog"/> — the folded/side-by-side row layout, the row→line map, the
/// search-hit overlay, narrow-width fallback, and fold-expansion survival across a stats refresh.
/// These lock the <em>current</em> visible behaviour so the DiffPaneComposer extraction (H1) can be
/// proven "visually unchanged": they must pass identically before and after the extraction.
/// </summary>
public class DiffPaneCompositionCharacterizationTests
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
        public IReadOnlyList<PrThread> Threads { get; init; } = [];

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult<PrIteration?>(new(2, "src", "tgt", "base"));
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);
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

    // A modified file: a change at top, 14 context lines, a change, 14 context lines, a change at
    // bottom. The two 14-line context runs each fold (radius 3 keeps 3 each side, hides 8), so the
    // unified projection is two folds — the shape the pins below lock.
    private static (string BaseText, string SourceText) TwoFoldFile()
    {
        var ctxTop = Enumerable.Range(0, 14).Select(i => $"ctx {i}");
        var ctxBot = Enumerable.Range(0, 14).Select(i => $"dtx {i}");
        string[] baseLines = ["top old", .. ctxTop, "mid old", .. ctxBot, "bot old"];
        string[] srcLines = ["top new", .. ctxTop, "mid new", .. ctxBot, "bot new"];
        return (string.Join("\n", baseLines) + "\n", string.Join("\n", srcLines) + "\n");
    }

    private static async Task<(DiffReviewDialog Detail, Dialog Dialog, RecordingUiPost Post)> Built(
        (string BaseText, string SourceText) file, int width = 120, RecordingUiPost? post = null)
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        source.Blobs[("/a.cs", "base")] = file.BaseText;
        source.Blobs[("/a.cs", "src")] = file.SourceText;
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var recorder = post ?? new RecordingUiPost();
        var detail = new DiffReviewDialog(App, vm, new FakeTextInput(), _ => { }, post: recorder);
        var dialog = detail.Build();
        dialog.Layout(new Size(width, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog, recorder);
    }

    private static DiffListDataSource Lines(DiffReviewDialog detail) =>
        Assert.IsType<DiffListDataSource>(detail.DiffPane.Source);

    [Fact]
    public async Task Unified_Folded_Rows_Pin()
    {
        var (detail, _, _) = await Built(TwoFoldFile());

        // (LineIndex, FoldId, HiddenCount) for every visible row, top to bottom. Two changes at the
        // ends and one in the middle keep 3 context lines either side of each change; the rest folds.
        (int? Line, int? Fold, int? Hidden)[] expected =
        [
            (0, null, null),   // Removed "top old"
            (1, null, null),   // Added "top new"
            (2, null, null),   // ctx 0
            (3, null, null),   // ctx 1
            (4, null, null),   // ctx 2
            (null, 0, 8),      // fold: ctx 3..10 hidden (8 lines)
            (13, null, null),  // ctx 11
            (14, null, null),  // ctx 12
            (15, null, null),  // ctx 13
            (16, null, null),  // Removed "mid old"
            (17, null, null),  // Added "mid new"
            (18, null, null),  // dtx 0
            (19, null, null),  // dtx 1
            (20, null, null),  // dtx 2
            (null, 1, 8),      // fold: dtx 3..10 hidden (8 lines)
            (29, null, null),  // dtx 11
            (30, null, null),  // dtx 12
            (31, null, null),  // dtx 13
            (32, null, null),  // Removed "bot old"
            (33, null, null),  // Added "bot new"
        ];

        Assert.Equal(expected.Length, detail.DiffRows.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var row = detail.DiffRows[i];
            Assert.Equal(expected[i].Line, row.LineIndex);
            Assert.Equal(expected[i].Fold, row.FoldId);
            Assert.Equal(expected[i].Hidden, row.HiddenCount);
        }

        // The two fold markers render the "N lines" placeholder text.
        var src = Lines(detail);
        Assert.Equal("    ··· 8 lines ···", src.Lines[5].DisplayText);
        Assert.Equal("    ··· 8 lines ···", src.Lines[14].DisplayText);
    }

    [Fact]
    public async Task Unified_RowMap_Resolves_Every_Visible_Line()
    {
        var (detail, _, _) = await Built(TwoFoldFile());

        // Every row carrying a line index round-trips: selecting it makes SelectedDiffLineIndex
        // report exactly that unified line (the invariant the single row map guarantees).
        for (var row = 0; row < detail.DiffRows.Count; row++)
        {
            if (detail.DiffRows[row].LineIndex is { } line)
            {
                detail.DiffPane.SelectedItem = row;
                Assert.Equal(line, detail.SelectedDiffLineIndex);
            }
        }

        // The lines hidden inside the two folds (3..10 and 21..28) appear in no visible row.
        var visible = detail.DiffRows.Select(r => r.LineIndex).OfType<int>().ToHashSet();
        foreach (var hidden in Enumerable.Range(5, 8).Concat(Enumerable.Range(21, 8)))
        {
            Assert.DoesNotContain(hidden, visible);
        }
    }

    [Fact]
    public async Task SideBySide_Pairing_And_Column_Width_Pin()
    {
        // A removed run of 2 against an added run of 1 leaves a blank right side on the surplus.
        var baseText = "keep\nold1\nold2\ntail\n";
        var srcText = "keep\nnew1\ntail\n";
        var (detail, dialog, _) = await Built((baseText, srcText), width: 100);

        dialog.NewKeyDownEvent(new Key('s'));
        Assert.True(detail.SideBySide);

        // Unified: Context keep(0), Removed old1(1), Added new1(2), Removed old2(3), Context tail(4).
        // Side-by-side pairs k-th removed↔k-th added, surplus removed rendered blank on the right.
        (int? Left, int? Right)[] expected =
        [
            (0, 0),      // context keep
            (1, 2),      // old1 ↔ new1
            (3, null),   // surplus removed — blank right side
            (4, 4),      // context tail
        ];
        Assert.Equal(expected.Length, detail.SideBySideRows.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Left, detail.SideBySideRows[i].LeftIndex);
            Assert.Equal(expected[i].Right, detail.SideBySideRows[i].RightIndex);
        }

        // Every composed row is exactly two equal-width columns joined by " │ ": same total length,
        // separator at column boundary = (length − separatorLength) / 2.
        var src = Lines(detail);
        var sep = SideBySideComposer.Separator;
        var totalLen = src.Lines[0].DisplayText.Length;
        var columnWidth = (totalLen - sep.Length) / 2;
        Assert.True(columnWidth >= 1);
        foreach (var styled in src.Lines)
        {
            Assert.Equal(totalLen, styled.DisplayText.Length);
            Assert.Equal(sep, styled.DisplayText.Substring(columnWidth, sep.Length));
        }
    }

    [Fact]
    public async Task Search_Hit_Decoration_Pin()
    {
        // "needle" on lines 1 and 3 of an all-added file (no folds — every line stays visible).
        var srcText = "alpha\nneedle one\nbeta\nneedle two\ngamma\n";
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = srcText;
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, new FakeTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();

        // Capture the undecorated composition per line before any search.
        var before = new Dictionary<int, StyledLine>();
        var srcBefore = Lines(detail);
        for (var i = 0; i < detail.DiffRows.Count; i++)
        {
            if (detail.DiffRows[i].LineIndex is { } line)
            {
                before[line] = srcBefore.Lines[i];
            }
        }

        detail.SearchPromptAction = () => "needle";
        dialog.NewKeyDownEvent(new Key('/'));
        Assert.Equal(2, detail.SearchMatchCount);

        var srcAfter = Lines(detail);
        var matched = new HashSet<int> { 1, 3 };
        for (var i = 0; i < detail.DiffRows.Count; i++)
        {
            if (detail.DiffRows[i].LineIndex is not { } line)
            {
                continue;
            }
            var styled = srcAfter.Lines[i];
            if (matched.Contains(line))
            {
                // The matched lines gain a search-hit run — decorated, so a fresh composition.
                Assert.Contains(styled.Runs, r => r.Style.SearchHit);
            }
            else
            {
                // Unmatched lines are the exact same composition instance: overlay, not recompose.
                Assert.Same(before[line], styled);
                Assert.DoesNotContain(styled.Runs, r => r.Style.SearchHit);
            }
        }
    }

    [Fact]
    public async Task Narrow_Width_Forces_Unified_Pin()
    {
        var baseText = "keep\nold\n";
        var srcText = "keep\nnew\n";
        var (detail, dialog, _) = await Built((baseText, srcText), width: 120);

        dialog.NewKeyDownEvent(new Key('s'));
        Assert.True(detail.SideBySide);
        Assert.True(detail.FileListVisible);
        var sbsRowCount = detail.DiffPane.Source!.Count;

        dialog.Layout(new Size(50, 24)); // below the file-list + side-by-side thresholds

        Assert.False(detail.FileListVisible);
        Assert.False(detail.SideBySide); // forced back to unified — too narrow for two columns
        // Unified rows: Context keep, Removed old, Added new — three rows, none paired.
        Assert.Equal(3, detail.DiffPane.Source!.Count);
        Assert.NotEqual(sbsRowCount, detail.DiffPane.Source!.Count);
        Assert.All(detail.DiffRows, r => Assert.Null(r.LeftIndex));
        Assert.All(detail.DiffRows, r => Assert.Null(r.RightIndex));
    }

    [Fact]
    public async Task SameFile_Refresh_Preserves_Fold_Expansion_Pin()
    {
        var post = new RecordingUiPost();
        var (detail, dialog, recorder) = await Built(TwoFoldFile(), post: post);

        // Expand the first fold, then confirm its hidden lines are now visible rows.
        var foldRow = detail.DiffRows.ToList().FindIndex(r => r.FoldId is not null);
        detail.DiffPane.SelectedItem = foldRow;
        dialog.NewKeyDownEvent(new Key('e'));
        var expandedVisible = detail.DiffRows.Select(r => r.LineIndex).OfType<int>().ToHashSet();
        Assert.Contains(5, expandedVisible); // a line hidden before the expand is now shown

        // A same-file stats refresh (background prefetch path) must not collapse the fold back.
        recorder.Posted.Clear();
        detail.OnStatsChanged();
        recorder.RunAll(); // drains RunQueuedStatsRefresh

        var afterRefresh = detail.DiffRows.Select(r => r.LineIndex).OfType<int>().ToHashSet();
        Assert.Contains(5, afterRefresh); // the expansion survived the refresh
        Assert.True(afterRefresh.SetEquals(expandedVisible)); // and the visible line set is unchanged
    }
}
