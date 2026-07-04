using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrDiffViewModelTests
{
    private sealed class FakeDiffSource : IPrDiffSource
    {
        public PrIteration? Iteration { get; set; } = new(2, "src", "tgt", "base");
        public IReadOnlyList<FileChange> Changes { get; set; } = [];
        public Dictionary<(string path, string commit), string> Blobs { get; } = new();
        public IReadOnlyList<PrThread> Threads { get; set; } = [];
        public (string path, int line, bool right, string text)? LastComment { get; private set; }

        public Task<PrIteration?> GetLatestIterationAsync(string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Iteration);

        public Task<IReadOnlyList<FileChange>> GetIterationChangesAsync(string repo, int prId, int iterationId, CancellationToken ct) =>
            Task.FromResult(Changes);

        public Task<string> GetFileContentAsync(string repo, string path, string commit, CancellationToken ct) =>
            Task.FromResult(Blobs.GetValueOrDefault((path, commit), ""));

        public Task<IReadOnlyList<PrThread>> GetThreadsAsync(string repo, int prId, CancellationToken ct) =>
            Task.FromResult(Threads);

        public Task AddLineCommentAsync(string repo, int prId, string path, int line, bool right, string text, CancellationToken ct)
        {
            LastComment = (path, line, right, text);
            return Task.CompletedTask;
        }
    }

    private static PullRequest Pr() =>
        new(10, "t", null, "active", false, "f", "main", "succeeded", "Jin", "repo-1", "web", [], [], "src");

    [Fact]
    public async Task Load_Lists_Changed_Files_And_Selects_First()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/a.cs", "base")] = "old\n";
        source.Blobs[("/a.cs", "src")] = "new\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal("/a.cs", vm.SelectedFile?.Path);
        Assert.NotNull(vm.CurrentDiff);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Added_File_Diffs_Empty_Base_Against_Source()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/new.cs", FileChangeKind.Add)],
        };
        source.Blobs[("/new.cs", "src")] = "a\nb\n"; // base returns "" (not in Blobs)
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Deleted_File_Diffs_Base_Against_Empty_Source()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/gone.cs", FileChangeKind.Delete)],
        };
        source.Blobs[("/gone.cs", "base")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.CurrentDiff!.Deletions);
    }

    [Fact]
    public async Task SelectFile_Recomputes_Current_Diff()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit), new FileChange("/b.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/b.cs", "base")] = "1\n";
        source.Blobs[("/b.cs", "src")] = "1\n2\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        await vm.SelectFileAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal("/b.cs", vm.SelectedFile?.Path);
        Assert.Equal(1, vm.CurrentDiff!.Additions);
    }

    [Fact]
    public async Task Existing_Threads_Map_To_Their_File_And_Line()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(1, PrThreadStatus.Active, [new PrComment(1, "Sam", "here", false)], "/a.cs", 2, null),
                new PrThread(2, PrThreadStatus.Active, [new PrComment(1, "Sam", "elsewhere", false)], "/other.cs", 5, null),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "x\ny\n";
        source.Blobs[("/a.cs", "src")] = "x\ny\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var onLine2 = vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Context, 2, 2, "y"));
        Assert.Single(onLine2);
        Assert.Equal(1, onLine2[0].Id);
        Assert.Empty(vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Context, 99, 99, "z")));
    }

    [Fact]
    public async Task Left_Side_Threads_Map_To_Removed_Lines()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
            Threads =
            [
                new PrThread(3, PrThreadStatus.Active, [new PrComment(1, "Sam", "deleted line note", false)], "/a.cs", null, 4),
            ],
        };
        source.Blobs[("/a.cs", "base")] = "a\nb\nc\nd\n";
        source.Blobs[("/a.cs", "src")] = "a\nb\nc\nd\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var onRemoved = vm.ThreadsForDiffLine(new DiffLine(DiffLineKind.Removed, 4, null, "d"));

        Assert.Single(onRemoved);
        Assert.Equal(3, onRemoved[0].Id);
    }

    [Fact]
    public async Task AddComment_On_Added_Line_Uses_Right_Side()
    {
        var source = new FakeDiffSource
        {
            Changes = [new FileChange("/a.cs", FileChangeKind.Edit)],
        };
        source.Blobs[("/a.cs", "base")] = "x\n";
        source.Blobs[("/a.cs", "src")] = "x\nadded\n";
        var vm = new PrDiffViewModel(source, Pr());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        // find the added line's index in the diff
        var addedIndex = vm.CurrentDiff!.Lines.ToList().FindIndex(l => l.Kind == DiffLineKind.Added);
        await vm.AddCommentAtLineAsync(addedIndex, "nice", TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastComment);
        Assert.Equal("/a.cs", source.LastComment!.Value.path);
        Assert.True(source.LastComment.Value.right);
        Assert.Equal(2, source.LastComment.Value.line); // new line number
    }

    [Fact]
    public async Task No_Iteration_Sets_Friendly_Error()
    {
        var source = new FakeDiffSource { Iteration = null };
        var vm = new PrDiffViewModel(source, Pr());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.Error);
    }
}
