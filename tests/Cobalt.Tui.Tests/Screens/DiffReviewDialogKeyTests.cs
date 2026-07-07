using System.Drawing;
using Cobalt.Core.Models;
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

    private sealed class FakeLauncher : IEditorLauncher
    {
        public Task<int> LaunchAsync(string path, CancellationToken ct) => Task.FromResult(0);
    }

    private static EditorService NoopEditor() => new(new FakeLauncher());

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
        public Task<string> GetFileContentAsync(string project, string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));
        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string project, string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);
        public Task AddLineCommentAsync(string project, string repo, int prId, string path, int line, bool right, string text, CancellationToken ct) =>
            Task.CompletedTask;
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

        var detail = new DiffReviewDialog(App, vm, NoopEditor(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(100, 24));
        dialog.SetFocus();
        return (detail, dialog);
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

        Assert.Equal(1, detail.FileIndex);
    }

    [Fact]
    public async Task LeftBracket_Retreats_The_File_Index()
    {
        var (detail, dialog) = await BuiltDialog();
        dialog.NewKeyDownEvent(new Key(']'));
        dialog.NewKeyDownEvent(new Key(']'));
        Assert.Equal(2, detail.FileIndex);

        dialog.NewKeyDownEvent(new Key('['));

        Assert.Equal(1, detail.FileIndex);
    }

    [Fact]
    public async Task Count_Then_RightBracket_Advances_By_Count()
    {
        var (detail, dialog) = await BuiltDialog();

        dialog.NewKeyDownEvent(new Key('3'));
        dialog.NewKeyDownEvent(new Key(']'));

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

        var detail = new DiffReviewDialog(App, vm, NoopEditor(), _ => { });
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

        var detail = new DiffReviewDialog(App, vm, NoopEditor(), _ => { });
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
        var detail = new DiffReviewDialog(App, vm, NoopEditor(), _ => { });
        var dialog = detail.Build();
        dialog.Layout(new Size(120, 24));
        dialog.SetFocus();
        dialog.NewKeyDownEvent(new Key('s'));

        var deletionRow = detail.SideBySideRows.ToList().FindIndex(r => r.LeftIndex is not null && r.RightIndex is null);
        detail.DiffPane.SelectedItem = deletionRow;

        Assert.Equal(detail.SideBySideRows[deletionRow].LeftIndex, detail.SelectedDiffLineIndex);
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
}
