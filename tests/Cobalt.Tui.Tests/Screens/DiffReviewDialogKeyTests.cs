using System.Drawing;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: builds the diff-review dialog and drives keys through the real
/// Terminal.Gui routing. Guards the router-driven navigation — j/k/gg/G scroll the focused
/// pane, Tab cycles panes, [ / ] change file (with count), q closes. Asserts on
/// SelectedItem / FileIndex, never Viewport.Y (TG 2.4.16).
/// </summary>
public class DiffReviewDialogKeyTests
{
    private static readonly IApplication App = Application.Create();

    /// <summary>Fake ITextInput for the migrated line-comment flow (ADR 0020); records every request.</summary>
    private sealed class FakeTextInput(string? textToReturn = null) : ITextInput
    {
        public List<TextInputRequest> Requests { get; } = [];

        public Task<string?> ReadAsync(TextInputRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(textToReturn);
        }
    }

    private static ITextInput NoopTextInput() => new FakeTextInput();

    private sealed class FakeDiffSource : IPrDiffSource
    {
        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();
        public IReadOnlyList<PrThread> Threads { get; set; } = [];

        public Task<PrIteration?> GetLatestIterationAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Iteration);
        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string project, string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);
        /// <summary>Blob fetches held open by path, so a test can render inside a select's await window.</summary>
        public Dictionary<string, TaskCompletionSource<string>> Gates { get; } = new(StringComparer.Ordinal);

        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Gates.TryGetValue(path, out var gate)
                ? gate.Task
                : Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));
        /// <summary>Makes the review-threads fetch fail the way a real ADO outage does.</summary>
        public bool FailThreads { get; set; }

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            FailThreads
                ? Task.FromException<IReadOnlyList<PrThread>>(new HttpRequestException("threads unavailable"))
                : Task.FromResult(Threads);
        public string? LastLineCommentText { get; private set; }

        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct)
        {
            LastLineCommentText = text;
            return Task.CompletedTask;
        }
        public Task ReplyToThreadAsync(string project, string repo, int prId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetThreadStatusAsync(string project, string repo, int prId, int threadId, PrThreadStatus status, CancellationToken ct) =>
            Task.CompletedTask;
        /// <summary>Makes a vote fail the way a real ADO rejection does (an expected error, ADR 0013).</summary>
        public bool FailVote { get; set; }

        public Task VoteAsync(string project, string repo, int prId, PrVote vote, CancellationToken ct) =>
            FailVote
                ? Task.FromException(new HttpRequestException("vote rejected"))
                : Task.CompletedTask;
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src", "Contoso.Web");

    // Four changed files; the first has a 30-line added diff so the diff pane has content.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltDialog()
    {
        var source = new FakeDiffSource
        {
            Changes =
            [
                new FileChange("/a.cs", FileChangeKind.Add),
                new FileChange("/b.cs", FileChangeKind.Add),
                new FileChange("/c.cs", FileChangeKind.Add),
                new FileChange("/d.cs", FileChangeKind.Add),
            ],
        };
        source.Blobs[("/a.cs", "src")] = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        return (detail, dialog);
    }

    /// <summary>A dialog whose review threads failed to load, with a second file to navigate to.</summary>
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog, PrDiffViewModel Vm)> ThreadsFailedDialog()
    {
        var source = new FakeDiffSource
        {
            FailThreads = true,
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "src")] = "alpha\n";
        source.Blobs[("/b.cs", "src")] = "beta\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.ThreadsUnavailable); // the fixture really did lose its threads

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        return (detail, dialog, vm);
    }

    [Fact]
    public async Task Unloadable_Threads_Stay_Visible_After_Navigating()
    {
        // A threads failure is permanent for the session (LoadAsync runs once), so every later
        // paint shows unmarked code that is indistinguishable from "this file has no comments".
        // The reviewer must be able to tell that markers are unknown rather than absent — at any
        // time, on any file — or they can approve a PR blind to its review comments.
        var (detail, _, vm) = await ThreadsFailedDialog();

        // Selecting a file publishes a diff, which is what used to mask the state. Changed fires
        // at the end of the select and a headless Application cannot service the Invoke it posts,
        // so observe that fault; the diff is published before Changed, which is the state here.
        var select = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotInitializedException>(() => select);
        Assert.NotNull(vm.CurrentDiff); // a diff is now on screen — the masking condition
        detail.RefreshStats();          // any later chrome refresh

        Assert.DoesNotContain("unresolved", detail.Title);
        Assert.Contains("comments unavailable", detail.Title);
    }

    [Fact]
    public async Task The_Title_Reports_Unresolved_Threads_When_They_Loaded()
    {
        // The counterpart: with threads loaded, the title keeps reporting the real count.
        var (detail, _) = await BuiltDialog();

        Assert.Contains("unresolved", detail.Title);
        Assert.DoesNotContain("comments unavailable", detail.Title);
    }

    [Fact]
    public async Task J_Moves_The_Focused_File_List()
    {
        var (detail, dialog) = await BuiltDialog();
        detail.FileList.SetFocus();

        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(1, detail.FileList.SelectedItem);
    }

    [Fact]
    public async Task Stats_Refresh_Does_Not_Rebuild_The_Diff_Pane()
    {
        // The background-prefetch stats path refreshes title totals + file-row stats but must
        // not re-tokenize/rebuild the (unchanged) displayed diff. A full Render always assigns a
        // new DiffListDataSource; the stats-only path must leave the existing instance in place.
        var (detail, _) = await BuiltDialog();
        var before = detail.DiffPane.Source;

        detail.RefreshStats();

        Assert.Same(before, detail.DiffPane.Source);
    }

    [Fact]
    public async Task Thread_Navigation_Follows_The_Displayed_File_Threads()
    {
        // ]t probed threads through vm.ThreadsForDiffLine, which scopes to SelectedFile — so
        // during a select it navigated the *next* file's threads over the displayed file's lines
        // and simply refused to move. Probing the displayed file's commented lines fixes both
        // that and the per-line O(threads) scan behind it.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
            Threads = [new PrThread(1, PrThreadStatus.Active, [], "/a.cs", 3, null)],
        };
        source.Blobs[("/a.cs", "src")] = "l0\nl1\nl2\n"; // added lines, new line numbers 1..3
        source.Gates["/b.cs"] = new TaskCompletionSource<string>();
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        _ = vm.SelectFileAsync(1, TestContext.Current.CancellationToken); // /b.cs's fetch hangs
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        detail.DiffPane.SetFocus();
        Assert.Equal("/a.cs", vm.CurrentDiffPath); // /a.cs is on screen, /b.cs is selected

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('t'));

        // /a.cs's thread is on new line 3 — the third of its three added lines.
        Assert.Equal(2, detail.SelectedDiffLineIndex);
    }

    [Fact]
    public async Task Mark_Viewed_Marks_The_File_On_Screen_Not_The_One_Being_Selected()
    {
        // Same drift as the header: during a select, SelectedFile is already the next file while
        // the previous one is still on screen. 'm' means "I have reviewed what I am looking at",
        // so it must mark the displayed file — marking the one the reviewer has not seen yet
        // silently drops a file out of their review.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "src")] = "alpha\n";
        source.Gates["/b.cs"] = new TaskCompletionSource<string>();
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        _ = vm.SelectFileAsync(1, TestContext.Current.CancellationToken); // /b.cs's fetch hangs
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        Assert.Equal("/b.cs", vm.SelectedFile?.Path);
        Assert.Equal("/a.cs", vm.CurrentDiffPath);

        dialog.NewKeyDownEvent(new Key('m'));

        Assert.True(vm.IsViewed("/a.cs"), "the file on screen should be marked viewed");
        Assert.False(vm.IsViewed("/b.cs"), "the file still loading should not be marked viewed");
    }

    [Fact]
    public async Task Mark_Viewed_Does_Not_Rebuild_The_Diff_Pane()
    {
        // m only changes the file-tree glyph for the current file; the displayed diff is
        // unchanged, so it must not pay the re-tokenize cost of a full diff-pane rebuild.
        var (detail, dialog) = await BuiltDialog();
        var before = detail.DiffPane.Source;

        dialog.NewKeyDownEvent(new Key('m'));

        Assert.Same(before, detail.DiffPane.Source);
    }

    [Fact]
    public async Task Mark_Unviewed_Does_Not_Rebuild_The_Diff_Pane()
    {
        var (detail, dialog) = await BuiltDialog();
        var before = detail.DiffPane.Source;

        dialog.NewKeyDownEvent(new Key('M'));

        Assert.Same(before, detail.DiffPane.Source);
    }

    // An edited file whose only change is at the top, so the 20-odd context lines below it fold
    // away (radius 3) and 'e' has something to expand.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> FoldedDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        var original = Enumerable.Range(0, 30).Select(i => $"var x{i} = {i};").ToList();
        source.Blobs[("/a.cs", "base")] = string.Join("\n", original) + "\n";
        original[0] = "var x0 = 99;";
        source.Blobs[("/a.cs", "src")] = string.Join("\n", original) + "\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        return (detail, dialog);
    }

    /// <summary>The composed line currently shown for each visible unified diff-line index.</summary>
    private static Dictionary<int, StyledLine> ComposedByLine(DiffReviewDialog detail)
    {
        var source = Assert.IsType<DiffListDataSource>(detail.DiffPane.Source);
        var map = new Dictionary<int, StyledLine>();
        for (var i = 0; i < detail.DiffRows.Count; i++)
        {
            if (detail.DiffRows[i].LineIndex is { } lineIndex)
            {
                map[lineIndex] = source.Lines[i];
            }
        }
        return map;
    }

    [Fact]
    public async Task The_Diff_Pane_Describes_The_File_Its_Diff_Came_From()
    {
        // SelectFileAsync moves SelectedFile to the new file immediately and only publishes that
        // file's diff once the fetch returns (PrDiffViewModel.SelectFileAsync). A render inside
        // that window — a comment or vote round-trip landing — would otherwise pair the new
        // file's identity with the previous file's diff: its path over the old stats, and its
        // comment markers painted onto the old file's lines.
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add), new FileChange("/b.cs", FileChangeKind.Add)],
            // A thread on /b.cs, right line 1 — the same line number /a.cs's diff has.
            Threads = [new PrThread(1, PrThreadStatus.Active, [], "/b.cs", 1, null)],
        };
        source.Blobs[("/a.cs", "src")] = "alpha\n";
        source.Gates["/b.cs"] = new TaskCompletionSource<string>();
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        // Left in flight deliberately: completing it would raise Changed, and a headless
        // Application cannot service the Invoke that posts.
        _ = vm.SelectFileAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal("/b.cs", vm.SelectedFile?.Path); // the cursor has moved
        Assert.Equal("/a.cs", vm.CurrentDiffPath);    // the diff on screen has not

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));

        Assert.Contains("/a.cs", detail.DiffHeader.Text);
        var shown = Assert.IsType<DiffListDataSource>(detail.DiffPane.Source);
        Assert.DoesNotContain(shown.Lines, l => l.DisplayText.Contains('●'));
    }

    [Fact]
    public async Task Mark_Viewed_Keeps_The_File_Header_After_A_Failed_Action()
    {
        // A failed vote leaves vm.Error set, and its message reaches the reviewer through the
        // message bar. The header's job is to say which file is on screen, so marking it viewed
        // must not replace the path/stats with a stale error from an unrelated action.
        var source = new FakeDiffSource
        {
            FailVote = true,
            Changes = [new FileChange("/a.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "src")] = "line 0\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        // Voted before Build subscribes: a headless Application cannot service the Invoke that
        // vm.Changed would post. The state under test is the same — Error set, a diff on screen.
        await vm.VoteAsync(PrVote.Approved, TestContext.Current.CancellationToken);
        Assert.NotNull(vm.Error); // the vote really did fail
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        // A full render already prefers the path over the error; the partial render must agree.
        var expected = detail.DiffHeader.Text;
        Assert.Contains("/a.cs", expected);

        dialog.NewKeyDownEvent(new Key('m'));

        // Compared whole, not just for the path, so the binary/too-large/side-by-side suffix
        // cannot be dropped either — m changes nothing the header reports.
        Assert.Equal(expected, detail.DiffHeader.Text);
    }

    [Fact]
    public async Task Expanding_A_Fold_Reuses_The_Lines_Already_Composed()
    {
        // 'e' changes only which lines are visible, so the lines that were already on screen must
        // come back as the same composition — re-tokenizing them is the cost this exists to avoid.
        var (detail, dialog) = await FoldedDialog();
        Assert.Contains(detail.DiffRows, r => r.FoldId is not null); // the fixture actually folds
        var before = ComposedByLine(detail);

        dialog.NewKeyDownEvent(new Key('e'));

        var after = ComposedByLine(detail);
        Assert.True(after.Count > before.Count, "expanding the fold should reveal more lines");
        foreach (var (lineIndex, styled) in before)
        {
            Assert.Same(styled, after[lineIndex]);
        }
    }

    [Fact]
    public async Task Tab_Flips_Focus_Between_Panes()
    {
        var (detail, dialog) = await BuiltDialog();
        detail.FileList.SetFocus();
        Assert.True(detail.FileList.HasFocus);

        dialog.NewKeyDownEvent(Key.Tab);

        Assert.True(detail.DiffPane.HasFocus);
    }

    [Fact]
    public async Task RightBracket_Advances_The_File_Index()
    {
        var (detail, dialog) = await BuiltDialog();

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('f'));

        Assert.Equal(1, detail.FileIndex);
    }

    [Fact]
    public async Task LeftBracket_Retreats_The_File_Index()
    {
        var (detail, dialog) = await BuiltDialog();
        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('f'));
        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('f'));
        Assert.Equal(2, detail.FileIndex);

        dialog.NewKeyDownEvent(new Key('['));
        dialog.NewKeyDownEvent(new Key('f'));

        Assert.Equal(1, detail.FileIndex);
    }

    [Fact]
    public async Task Count_Then_RightBracket_Advances_By_Count()
    {
        var (detail, dialog) = await BuiltDialog();

        dialog.NewKeyDownEvent(new Key('3'));
        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('f'));

        Assert.Equal(3, detail.FileIndex); // 0 → 3 (four files, clamped within range)
    }

    [Fact]
    public async Task G_On_Diff_Pane_Jumps_To_Last_Line_And_gg_Back()
    {
        var (detail, dialog) = await BuiltDialog();
        detail.DiffPane.SetFocus();
        var last = detail.DiffPane.Source!.Count - 1;
        Assert.True(last > 0);

        dialog.NewKeyDownEvent(new Key('G'));
        Assert.Equal(last, detail.DiffPane.SelectedItem);

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('g'));
        Assert.Equal(0, detail.DiffPane.SelectedItem);
    }

    [Fact]
    public async Task CtrlD_On_Diff_Pane_Moves_A_Half_Page()
    {
        var (detail, dialog) = await BuiltDialog();
        detail.DiffPane.SetFocus();
        var half = Math.Max(1, detail.DiffPane.Viewport.Height / 2);

        dialog.NewKeyDownEvent(new Key('d').WithCtrl);

        Assert.Equal(half, detail.DiffPane.SelectedItem);
    }

    [Fact]
    public async Task Q_Closes_The_Dialog()
    {
        var (detail, dialog) = await BuiltDialog();
        var closed = false;
        detail.CloseAction = () => closed = true;

        dialog.NewKeyDownEvent(new Key('q'));

        Assert.True(closed);
    }

    // ---- directory-tree file list (Item 1) ----

    // Two files under src/Web plus a root README, so the tree has a folder row.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltTreeDialog()
    {
        var source = new FakeDiffSource
        {
            Changes =
            [
                new FileChange("/src/Web/Home.cs", FileChangeKind.Add),  // fileIndex 0
                new FileChange("/src/Web/About.cs", FileChangeKind.Add), // fileIndex 1
                new FileChange("/README.md", FileChangeKind.Add),        // fileIndex 2
            ],
        };
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        detail.FileList.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task Load_Highlights_The_Displayed_Files_Leaf_Not_A_Folder()
    {
        var (detail, _) = await BuiltTreeDialog();

        var selected = detail.Rows[detail.FileList.SelectedItem!.Value];
        Assert.Equal(FileTreeRowKind.File, selected.Kind);
        Assert.Equal(detail.FileIndex, selected.FileIndex);
    }

    [Fact]
    public async Task NextFile_Steps_Over_Folder_Rows_And_Only_Lands_On_Files()
    {
        var (detail, dialog) = await BuiltTreeDialog();

        // Walk to the end; every landing is a file leaf, never a folder header.
        for (var i = 0; i < 5; i++)
        {
            dialog.NewKeyDownEvent(new Key(']'));
            dialog.NewKeyDownEvent(new Key('f'));
            Assert.Equal(FileTreeRowKind.File, detail.Rows[detail.FileList.SelectedItem!.Value].Kind);
        }

        Assert.Equal(2, detail.FileIndex); // clamped at README (the last visible leaf)
    }

    [Fact]
    public async Task Enter_On_A_Folder_Row_Collapses_It()
    {
        var (detail, dialog) = await BuiltTreeDialog();
        var folder = detail.Rows.ToList().FindIndex(r => r.Kind == FileTreeRowKind.Directory);
        detail.FileList.SelectedItem = folder;
        var before = detail.FileList.Source!.Count;

        dialog.NewKeyDownEvent(Key.Enter);

        Assert.True(detail.Rows[folder].Collapsed);
        Assert.True(detail.FileList.Source!.Count < before); // the folder's leaves are hidden
    }

    [Fact]
    public async Task Z_Toggles_The_Folder_Under_The_Cursor()
    {
        var (detail, dialog) = await BuiltTreeDialog();
        var folder = detail.Rows.ToList().FindIndex(r => r.Kind == FileTreeRowKind.Directory);
        detail.FileList.SelectedItem = folder;
        var expanded = detail.FileList.Source!.Count;

        dialog.NewKeyDownEvent(new Key('z'));
        Assert.True(detail.FileList.Source!.Count < expanded);

        dialog.NewKeyDownEvent(new Key('z'));
        Assert.Equal(expanded, detail.FileList.Source!.Count);
    }

    // ---- side-by-side toggle (Item 3) ----

    // A single modified file: "keep" unchanged, "old" → "new". Unified diff is
    // [Context keep, Removed old, Added new]; side-by-side pairs the removed+added.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltModifiedDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        source.Blobs[("/a.cs", "base")] = "keep\nold\n";
        source.Blobs[("/a.cs", "src")] = "keep\nnew\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task S_Toggles_Side_By_Side_And_Back()
    {
        var (detail, dialog) = await BuiltModifiedDialog();
        var unifiedRows = detail.DiffPane.Source!.Count; // 3 unified lines

        dialog.NewKeyDownEvent(new Key('s'));
        Assert.True(detail.SideBySide);
        Assert.Equal(2, detail.DiffPane.Source!.Count); // context row + one paired modify row

        dialog.NewKeyDownEvent(new Key('s'));
        Assert.False(detail.SideBySide);
        Assert.Equal(unifiedRows, detail.DiffPane.Source!.Count);
    }

    [Fact]
    public async Task Comment_On_A_Paired_Row_Anchors_To_The_New_Side_Line()
    {
        var (detail, dialog) = await BuiltModifiedDialog();
        dialog.NewKeyDownEvent(new Key('s'));

        // The paired modify row maps to both sides; the comment anchors on the new (right) line.
        var pairedRow = detail.SideBySideRows.ToList().FindIndex(r => r.LeftIndex is not null && r.RightIndex is not null);
        detail.DiffPane.SelectedItem = pairedRow;

        var row = detail.SideBySideRows[pairedRow];
        Assert.Equal(row.RightIndex, detail.SelectedDiffLineIndex); // new side wins
    }

    [Fact]
    public async Task Comment_On_A_Deletion_Only_Row_Anchors_To_The_Old_Side_Line()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        source.Blobs[("/a.cs", "base")] = "keep\ngone\n";
        source.Blobs[("/a.cs", "src")] = "keep\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        dialog.NewKeyDownEvent(new Key('s'));

        var deletionRow = detail.SideBySideRows.ToList().FindIndex(r => r.LeftIndex is not null && r.RightIndex is null);
        detail.DiffPane.SelectedItem = deletionRow;

        Assert.Equal(detail.SideBySideRows[deletionRow].LeftIndex, detail.SelectedDiffLineIndex);
    }

    // ---- context folding (Item 10) ----

    // A modified file with 8 identical leading context lines then one changed line, so the
    // distant top context run (indices 0..7, > radius 3) collapses into a fold marker.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltFoldedDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        var head = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"ctx {i}"));
        source.Blobs[("/a.cs", "base")] = head + "\nold\n";
        source.Blobs[("/a.cs", "src")] = head + "\nnew\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task Fold_Collapses_Distant_Context_By_Default()
    {
        var (detail, _) = await BuiltFoldedDialog();

        // 10 unified lines (8 context + removed + added) collapse: the top 5 context lines fold.
        Assert.NotNull(detail.DiffRows[0].FoldId);
        Assert.Equal(5, detail.DiffRows[0].HiddenCount);
        Assert.Equal(6, detail.DiffRows.Count); // marker + 3 kept context + removed + added
    }

    [Fact]
    public async Task Folded_Row_Anchors_To_Its_Original_Line_Not_Its_Row_Index()
    {
        var (detail, _) = await BuiltFoldedDialog();

        // Rows: [fold marker, line 5, line 6, line 7, removed 8, added 9]. Row 1's cursor anchors
        // to unified line 5 — the invariant the single row map exists to guarantee (row ≠ line).
        Assert.Equal(5, detail.DiffRows[1].LineIndex);
        detail.DiffPane.SelectedItem = 1;
        Assert.Equal(5, detail.SelectedDiffLineIndex);
    }

    [Fact]
    public async Task Search_Match_Hidden_In_A_Fold_Auto_Expands_And_Selects()
    {
        var (detail, dialog) = await BuiltFoldedDialog();
        detail.SearchPromptAction = () => "ctx 2"; // line index 2 sits inside the collapsed top fold

        dialog.NewKeyDownEvent(new Key('/'));

        Assert.Equal(1, detail.SearchMatchCount);
        Assert.DoesNotContain(detail.DiffRows, r => r.FoldId is not null); // fold expanded to reveal it
        Assert.Equal(2, detail.SelectedDiffLineIndex);
    }

    [Fact]
    public async Task Selecting_A_Fold_Marker_Anchors_To_Nothing()
    {
        var (detail, _) = await BuiltFoldedDialog();
        var foldRow = detail.DiffRows.ToList().FindIndex(r => r.FoldId is not null);
        detail.DiffPane.SelectedItem = foldRow;

        Assert.Equal(-1, detail.SelectedDiffLineIndex); // anchorless — comment/thread guards bail
    }

    [Fact]
    public async Task E_On_A_Fold_Marker_Expands_That_Fold()
    {
        var (detail, dialog) = await BuiltFoldedDialog();
        var foldRow = detail.DiffRows.ToList().FindIndex(r => r.FoldId is not null);
        detail.DiffPane.SelectedItem = foldRow;

        dialog.NewKeyDownEvent(new Key('e'));

        Assert.DoesNotContain(detail.DiffRows, r => r.FoldId is not null);
    }

    [Fact]
    public async Task Shift_E_Expands_Every_Fold()
    {
        var (detail, dialog) = await BuiltFoldedDialog();
        var folded = detail.DiffRows.Count;

        dialog.NewKeyDownEvent(new Key('E'));

        Assert.Equal(10, detail.DiffRows.Count); // every unified line now visible
        Assert.True(detail.DiffRows.Count > folded);
        Assert.DoesNotContain(detail.DiffRows, r => r.FoldId is not null);
    }

    [Fact]
    public async Task Side_By_Side_Does_Not_Fold()
    {
        var (detail, dialog) = await BuiltFoldedDialog();
        dialog.NewKeyDownEvent(new Key('s'));

        Assert.True(detail.SideBySide);
        Assert.DoesNotContain(detail.DiffRows, r => r.FoldId is not null); // full context in SBS
    }

    // ---- diff search (Item 7) ----

    // An added file with the term "needle" on two lines (indices 1 and 3).
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltSearchDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "alpha\nneedle one\nbeta\nneedle two\ngamma\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task Slash_Jumps_To_The_First_Match_And_n_N_Cycle()
    {
        var (detail, dialog) = await BuiltSearchDialog();
        detail.SearchPromptAction = () => "needle";

        dialog.NewKeyDownEvent(new Key('/'));
        Assert.Equal(2, detail.SearchMatchCount);
        Assert.Equal(1, detail.SelectedDiffLineIndex); // first match line

        dialog.NewKeyDownEvent(new Key('n'));
        Assert.Equal(3, detail.SelectedDiffLineIndex); // next match line

        dialog.NewKeyDownEvent(new Key('N'));
        Assert.Equal(1, detail.SelectedDiffLineIndex); // back to first
    }

    // ---- hunk / thread / unviewed navigation (Item 2) ----

    // Two changed lines (top + bottom) separated by 5 context lines: two hunks at line 0 and 7.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltTwoHunkDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        source.Blobs[("/a.cs", "base")] = "a\nb\nc\nd\ne\nf\ng\n";
        source.Blobs[("/a.cs", "src")] = "A\nb\nc\nd\ne\nf\nG\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task Bracket_c_Navigates_Hunks()
    {
        var (detail, dialog) = await BuiltTwoHunkDialog();

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('c'));
        Assert.Equal(7, detail.SelectedDiffLineIndex); // second hunk (removed 'g')

        dialog.NewKeyDownEvent(new Key('['));
        dialog.NewKeyDownEvent(new Key('c'));
        Assert.Equal(0, detail.SelectedDiffLineIndex); // back to first hunk
    }

    [Fact]
    public async Task Bracket_t_Navigates_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Add)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "c1", false)], "/a.cs", RightLine: 2, LeftLine: null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(2, "Sam", "c2", false)], "/a.cs", RightLine: 5, LeftLine: null),
            ],
        };
        source.Blobs[("/a.cs", "src")] = "l0\nl1\nl2\nl3\nl4\nl5\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('t'));
        Assert.Equal(1, detail.SelectedDiffLineIndex); // right line 2 → index 1

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('t'));
        Assert.Equal(4, detail.SelectedDiffLineIndex); // right line 5 → index 4
    }

    [Fact]
    public async Task Bracket_v_Moves_To_Next_Unviewed_Skipping_Viewed()
    {
        var (detail, dialog) = await BuiltDialog(); // a, b, c, d at root
        detail.FileList.SetFocus();

        // Mark file b viewed, then return to a.
        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('f'));
        Assert.Equal(1, detail.FileIndex);
        dialog.NewKeyDownEvent(new Key('m'));
        dialog.NewKeyDownEvent(new Key('['));
        dialog.NewKeyDownEvent(new Key('f'));
        Assert.Equal(0, detail.FileIndex);

        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key('v'));

        Assert.Equal(2, detail.FileIndex); // skipped the viewed b, landed on c
    }

    // ---- mark viewed / stats (Items 8, 5) ----

    [Fact]
    public async Task M_Marks_The_Current_File_Viewed()
    {
        var (detail, dialog) = await BuiltModifiedDialog();
        Assert.False(detail.Rows.Single(r => r.Kind == FileTreeRowKind.File).Viewed);

        dialog.NewKeyDownEvent(new Key('m'));

        Assert.True(detail.Rows.Single(r => r.Kind == FileTreeRowKind.File).Viewed);
    }

    [Fact]
    public async Task Shift_M_Marks_The_Current_File_Unviewed()
    {
        var (detail, dialog) = await BuiltModifiedDialog();
        dialog.NewKeyDownEvent(new Key('m'));
        Assert.True(detail.Rows.Single(r => r.Kind == FileTreeRowKind.File).Viewed);

        dialog.NewKeyDownEvent(new Key('M'));

        Assert.False(detail.Rows.Single(r => r.Kind == FileTreeRowKind.File).Viewed);
    }

    [Fact]
    public async Task File_Row_Shows_Diff_Stats()
    {
        var (detail, _) = await BuiltModifiedDialog(); // one line removed + one added

        var row = detail.Rows.Single(r => r.Kind == FileTreeRowKind.File);
        Assert.Equal(1, row.Additions);
        Assert.Equal(1, row.Deletions);
    }

    // ---- vote (Item 4) ----

    [Fact]
    public async Task V_Applies_The_Chosen_Vote()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        PrVote? applied = null;
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { })
        {
            VoteChooser = (_, labels) => labels.ToList().IndexOf("reject"),
            VoteAction = v => applied = v,
        };
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('v'));

        Assert.Equal(PrVote.Rejected, applied); // picker choice maps to the right vote
    }

    // ---- unresolved-file filter (Item 6) ----

    [Fact]
    public async Task T_Filters_To_Files_With_Unresolved_Threads()
    {
        var source = new FakeDiffSource
        {
            Changes =
            [
                new FileChange("/a.cs", FileChangeKind.Add), // index 0
                new FileChange("/b.cs", FileChangeKind.Add), // index 1 — has the thread
                new FileChange("/c.cs", FileChangeKind.Add), // index 2
            ],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "fix", false)], "/b.cs", RightLine: 1, LeftLine: null),
            ],
        };
        foreach (var p in new[] { "/a.cs", "/b.cs", "/c.cs" })
        {
            source.Blobs[(p, "src")] = "x\n";
        }
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.FileList.SetFocus();

        dialog.NewKeyDownEvent(new Key('T'));

        var files = detail.Rows.Where(r => r.Kind == FileTreeRowKind.File).ToList();
        Assert.Single(files);
        Assert.Equal("/b.cs", files[0].NodePath);
        Assert.True(files[0].HasUnresolved);
        Assert.Equal(1, detail.FileIndex); // current file resolved to b's identity in vm.Files
    }

    // ---- responsive layout (Item 2) ----

    [Fact]
    public async Task Narrowing_Hides_The_File_List_And_Forces_Unified()
    {
        var (detail, dialog) = await BuiltModifiedDialog(); // laid out at 120 wide
        dialog.NewKeyDownEvent(new Key('s'));
        Assert.True(detail.SideBySide);
        Assert.True(detail.FileListVisible);

        dialog.Layout(new Size(48, 24)); // shrink below the file-list threshold

        Assert.False(detail.FileListVisible);
        Assert.False(detail.SideBySide); // fell back to unified — too narrow for two columns
    }

    [Fact]
    public async Task Widening_Restores_The_File_List()
    {
        var (detail, dialog) = await BuiltModifiedDialog();
        dialog.Layout(new Size(48, 24));
        Assert.False(detail.FileListVisible);

        dialog.Layout(new Size(120, 24));

        Assert.True(detail.FileListVisible);
    }

    // ---- view existing comments on a diff line ----

    [Fact]
    public async Task O_On_A_Threaded_Line_Shows_The_Existing_Comments()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(7, PrThreadStatus.Active,
                    [new PrComment(1, "Sam", "looks good to me", false)], "/a.cs", RightLine: 2, LeftLine: null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "keep\nold\n";
        source.Blobs[("/a.cs", "src")] = "keep\nnew\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        string? shown = null;
        detail.ViewThreadAction = t => shown = t;
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();

        // Select the added line (new line 2), which the thread anchors to, then "open" it.
        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);
        detail.DiffPane.SelectedItem = addedIndex;
        dialog.NewKeyDownEvent(new Key('o'));

        Assert.NotNull(shown);
        Assert.Contains("looks good to me", shown);
        Assert.Contains("#7", shown);
    }

    [Fact]
    public async Task O_On_An_Unthreaded_Line_Shows_Nothing()
    {
        var (detail, dialog) = await BuiltModifiedDialog(); // no threads
        detail.DiffPane.SetFocus();
        var shown = false;
        detail.ViewThreadAction = _ => shown = true;

        dialog.NewKeyDownEvent(new Key('o'));

        Assert.False(shown);
    }

    [Fact]
    public async Task Comment_On_A_Fold_Marker_Is_Refused_Not_Silently_Dropped()
    {
        // 20 identical leading lines fold to a marker at row 0, so the cursor there
        // anchors to no diff line; commenting must be refused (not silently lost).
        var captured = new List<string>();
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Edit)] };
        var ctx = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"ctx{i}"));
        source.Blobs[("/a.cs", "base")] = ctx + "\nold\n";
        source.Blobs[("/a.cs", "src")] = ctx + "\nnew\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), captured.Add);
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();

        detail.DiffPane.SelectedItem = 0;
        Assert.Equal(-1, detail.SelectedDiffLineIndex); // row 0 is a fold marker

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.Contains(captured, m => m.Contains("no diff line here"));
        Assert.DoesNotContain(captured, m => m.Contains("line comment added"));
    }

    // ---- comment via ITextInput (ADR 0020) ----

    // The success path only asserts the request: PrDiffViewModel.AddCommentAtLineAsync fires
    // Changed (→ app.Invoke) before calling the store, and this suite's App never
    // Application.Init()s (no test in the file does — headless-by-construction), so a real
    // mutation can't complete synchronously here. Pre-existing (identical under the old
    // $EDITOR path); the returned-string-is-used behavior is covered where the harness
    // permits it (WorkItemActionsTests, ThreadViewDialogTests).
    [Fact]
    public async Task C_Reads_Via_TextInput_With_The_Expected_Request()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var textInput = new FakeTextInput("looks good");
        var detail = new DiffReviewDialog(App, vm, textInput, _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        detail.DiffPane.SelectedItem = 0;

        dialog.NewKeyDownEvent(new Key('c'));

        var request = Assert.Single(textInput.Requests);
        Assert.Equal("comment", request.Title);
        Assert.False(request.SingleLine);
    }

    [Fact]
    public async Task C_Cancelled_TextInput_Posts_No_Comment()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var textInput = new FakeTextInput(null);
        var detail = new DiffReviewDialog(App, vm, textInput, _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        detail.DiffPane.SelectedItem = 0;

        dialog.NewKeyDownEvent(new Key('c'));

        Assert.Single(textInput.Requests);
        Assert.Null(source.LastLineCommentText);
    }

    // ---- Item 3: Enter on the diff pane opens the thread (not close the dialog) ----

    [Fact]
    public async Task Enter_On_The_Diff_Pane_Opens_The_Thread_And_Does_Not_Close()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(7, PrThreadStatus.Active,
                    [new PrComment(1, "Sam", "looks good to me", false)], "/a.cs", RightLine: 2, LeftLine: null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "keep\nold\n";
        source.Blobs[("/a.cs", "src")] = "keep\nnew\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        string? shown = null;
        var closed = false;
        detail.ViewThreadAction = t => shown = t;
        detail.CloseAction = () => closed = true;
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();

        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);
        detail.DiffPane.SelectedItem = addedIndex;
        dialog.NewKeyDownEvent(Key.Enter);

        Assert.NotNull(shown);           // Enter opened the thread overlay (via the seam)
        Assert.Contains("#7", shown);
        Assert.False(closed);            // Enter did not close the dialog
    }

    // ---- Item 2: h / l horizontally scroll the diff pane (count-aware) ----

    // A single added file with two 200-char lines, so the diff pane content is far wider
    // than the pane and horizontal scrolling has room to move.
    private static async Task<(DiffReviewDialog Detail, Dialog Dialog)> BuiltWideDialog()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = new string('x', 200) + "\n" + new string('y', 200) + "\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        detail.DiffPane.SetFocus();
        return (detail, dialog);
    }

    [Fact]
    public async Task L_And_H_Scroll_The_Diff_Pane_Horizontally_Count_Aware_And_Clamp_At_Zero()
    {
        var (detail, dialog) = await BuiltWideDialog();
        Assert.Equal(0, detail.DiffPane.Viewport.X);

        dialog.NewKeyDownEvent(new Key('l'));
        Assert.Equal(1, detail.DiffPane.Viewport.X); // one column right

        dialog.NewKeyDownEvent(new Key('5'));
        dialog.NewKeyDownEvent(new Key('l'));
        Assert.Equal(6, detail.DiffPane.Viewport.X); // count-aware: +5

        dialog.NewKeyDownEvent(new Key('h'));
        Assert.Equal(5, detail.DiffPane.Viewport.X); // one column left

        dialog.NewKeyDownEvent(new Key('9'));
        dialog.NewKeyDownEvent(new Key('h'));
        Assert.Equal(0, detail.DiffPane.Viewport.X); // clamps at 0
    }

    // ---- Item 4: viewed indicator is a leading [✓] / [ ] column ----

    [Fact]
    public async Task Viewed_File_Row_Leads_With_A_Check_Column_Unviewed_With_A_Blank()
    {
        var (detail, dialog) = await BuiltDialog(); // a, b, c, d at root; file 0 (a.cs) is current

        dialog.NewKeyDownEvent(new Key('m')); // mark the current file (a.cs) viewed

        var strings = detail.FileList.Source!.ToList().Cast<string>().ToList();
        var viewedRow = strings.First(s => s.Contains("a.cs")).TrimStart();
        var unviewedRow = strings.First(s => s.Contains("b.cs")).TrimStart();
        Assert.StartsWith("[✓]", viewedRow);
        Assert.StartsWith("[ ]", unviewedRow);
        Assert.DoesNotContain("✓", unviewedRow);
    }

    // ---- Item 1: inline search bar replacing the $EDITOR prompt ----

    [Fact]
    public async Task Slash_Shows_The_Inline_Search_Bar_And_Focuses_It()
    {
        var (detail, dialog) = await BuiltSearchDialog(); // no SearchPromptAction seam → real bar path
        Assert.False(detail.SearchBar.Visible);

        dialog.NewKeyDownEvent(new Key('/'));

        Assert.True(detail.SearchBar.Visible);
        Assert.True(detail.SearchBar.HasFocus);
    }

    [Fact]
    public async Task Bar_Text_Then_Enter_Runs_The_Search_And_Hides_The_Bar()
    {
        var (detail, dialog) = await BuiltSearchDialog();
        dialog.NewKeyDownEvent(new Key('/'));
        detail.SearchBar.Text = "needle";

        dialog.NewKeyDownEvent(Key.Enter);

        Assert.Equal(2, detail.SearchMatchCount);
        Assert.Equal(1, detail.SelectedDiffLineIndex); // diff cursor on the first match line
        Assert.False(detail.SearchBar.Visible);        // bar hidden after applying
    }

    [Fact]
    public async Task Esc_In_The_Bar_Hides_It_And_Clears_The_Search()
    {
        var (detail, dialog) = await BuiltSearchDialog();
        dialog.NewKeyDownEvent(new Key('/'));
        detail.SearchBar.Text = "needle";
        dialog.NewKeyDownEvent(Key.Enter);
        Assert.Equal(2, detail.SearchMatchCount);

        dialog.NewKeyDownEvent(new Key('/')); // reopen the bar
        dialog.NewKeyDownEvent(Key.Esc);

        Assert.False(detail.SearchBar.Visible);
        Assert.Equal(0, detail.SearchMatchCount); // cleared
    }

    // ---- Item 5: g b opens the PR's source branch in the browser ----

    [Fact]
    public async Task G_B_Opens_The_Source_Branch_Url_In_The_Browser()
    {
        var source = new FakeDiffSource { Changes = [new FileChange("/a.cs", FileChangeKind.Add)] };
        source.Blobs[("/a.cs", "src")] = "x\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var context = new AdoContext
        {
            Name = "dev",
            OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
            Project = "Contoso.Web",
        };
        string? opened = null;
        var detail = new DiffReviewDialog(App, vm, NoopTextInput(), _ => { }, context)
        {
            OpenUrlAction = u => opened = u,
        };
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('b'));

        Assert.NotNull(opened);
        Assert.Contains("_git/", opened);
        Assert.Contains("version=GB", opened);
    }

    [Fact]
    public async Task Search_Bar_Hides_When_Focus_Leaves_It_Without_Enter_Or_Esc()
    {
        var (detail, dialog) = await BuiltSearchDialog();
        dialog.NewKeyDownEvent(new Key('/'));
        Assert.True(detail.SearchBar.Visible);
        Assert.True(detail.SearchBar.HasFocus);

        detail.DiffPane.SetFocus(); // focus leaves the bar (Tab/click equivalent), no Enter/Esc

        Assert.False(detail.SearchBar.Visible); // hidden, not orphaned with stale text
    }
}
