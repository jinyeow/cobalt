using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The pure file-list tree projection (ADR 0004): changed files → a flat list of
/// display rows (directory headers + file leaves) with a row→fileIndex mapping so
/// the dialog can navigate leaves and skip headers. Fully unit-tested without
/// Terminal.Gui.
/// </summary>
public class FileTreeTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Flatten_Groups_Files_Under_A_Directory_And_Shows_The_Basename()
    {
        FileChange[] files =
        [
            new("/src/Web/Home.cs", FileChangeKind.Edit),
            new("/src/Web/About.cs", FileChangeKind.Add),
        ];

        var rows = FileTree.Flatten(files, None);

        var dir = Assert.Single(rows, r => r.Kind == FileTreeRowKind.Directory);
        Assert.Equal("src/Web", dir.Label); // single-child dir chain compressed
        Assert.Equal(0, dir.Depth);

        var fileRows = rows.Where(r => r.Kind == FileTreeRowKind.File).ToList();
        Assert.Equal(["About.cs", "Home.cs"], fileRows.Select(r => r.Label)); // sorted alpha
        Assert.Equal(1, fileRows[0].Depth); // indented under the directory
        Assert.Equal([1, 0], fileRows.Select(r => r.FileIndex)); // mapping preserved to source order
    }

    [Fact]
    public void Collapsed_Directory_Hides_Its_Children()
    {
        FileChange[] files =
        [
            new("/src/Web/Home.cs", FileChangeKind.Edit),
            new("/README.md", FileChangeKind.Edit),
        ];

        var expanded = FileTree.Flatten(files, None);
        var dirPath = Assert.Single(expanded, r => r.Kind == FileTreeRowKind.Directory).NodePath;

        var collapsed = FileTree.Flatten(files, new HashSet<string> { dirPath });

        var dir = Assert.Single(collapsed, r => r.Kind == FileTreeRowKind.Directory);
        Assert.True(dir.Collapsed);
        // The directory's file is hidden; only the root-level README leaf remains.
        var leaves = collapsed.Where(r => r.Kind == FileTreeRowKind.File).ToList();
        Assert.Equal(["README.md"], leaves.Select(r => r.Label));
    }

    [Fact]
    public void Root_Level_File_Sits_At_Depth_Zero_With_A_Mapping()
    {
        FileChange[] files = [new("/README.md", FileChangeKind.Add)];

        var rows = FileTree.Flatten(files, None);

        var row = Assert.Single(rows);
        Assert.Equal(FileTreeRowKind.File, row.Kind);
        Assert.Equal("README.md", row.Label);
        Assert.Equal(0, row.Depth);
        Assert.Equal(0, row.FileIndex);
        Assert.Equal(FileChangeKind.Add, row.ChangeType);
    }

    [Fact]
    public void Directories_Sort_Before_Files_At_The_Same_Level()
    {
        FileChange[] files =
        [
            new("/zebra.cs", FileChangeKind.Edit),   // root file
            new("/alpha/one.cs", FileChangeKind.Edit), // root dir
        ];

        var rows = FileTree.Flatten(files, None);

        Assert.Equal(FileTreeRowKind.Directory, rows[0].Kind);
        Assert.Equal("alpha", rows[0].Label);
        Assert.Equal(FileTreeRowKind.File, rows[^1].Kind);
        Assert.Equal("zebra.cs", rows[^1].Label); // root file sorts after the directory
    }

    [Fact]
    public void Deeply_Nested_Distinguishing_Filename_Is_Shown_In_Full()
    {
        // The motivating case: a long path whose flat form left-truncated to
        // "…riable.networkSecurityGroups.json". The basename must be intact.
        FileChange[] files =
        [
            new("/infra/modules/network/NsgVariable.networkSecurityGroups.json", FileChangeKind.Edit),
        ];

        var rows = FileTree.Flatten(files, None);

        var leaf = Assert.Single(rows, r => r.Kind == FileTreeRowKind.File);
        Assert.Equal("NsgVariable.networkSecurityGroups.json", leaf.Label);
    }

    [Fact]
    public void No_Annotations_Path_Is_Identical_To_Before()
    {
        FileChange[] files =
        [
            new("/src/Web/Home.cs", FileChangeKind.Edit),
            new("/README.md", FileChangeKind.Edit),
        ];

        var rows = FileTree.Flatten(files, None);

        Assert.All(rows, r =>
        {
            Assert.Null(r.Additions);
            Assert.Null(r.Deletions);
            Assert.False(r.Viewed);
            Assert.False(r.HasUnresolved);
        });
    }

    [Fact]
    public void Annotations_Flow_Onto_The_Matching_Leaf_Rows()
    {
        FileChange[] files =
        [
            new("/src/Web/Home.cs", FileChangeKind.Edit),
            new("/README.md", FileChangeKind.Edit),
        ];
        var annotations = new Dictionary<string, FileAnnotation>
        {
            ["/src/Web/Home.cs"] = new(Additions: 4, Deletions: 1, Viewed: true, HasUnresolved: false),
            ["/README.md"] = new(Additions: 0, Deletions: 2, Viewed: false, HasUnresolved: true),
        };

        var rows = FileTree.Flatten(files, None, annotations);

        var home = Assert.Single(rows, r => r.Label == "Home.cs");
        Assert.Equal(4, home.Additions);
        Assert.Equal(1, home.Deletions);
        Assert.True(home.Viewed);
        Assert.False(home.HasUnresolved);

        var readme = Assert.Single(rows, r => r.Label == "README.md");
        Assert.Equal(0, readme.Additions);
        Assert.Equal(2, readme.Deletions);
        Assert.False(readme.Viewed);
        Assert.True(readme.HasUnresolved);
    }

    [Fact]
    public void Unannotated_Leaf_Keeps_Defaults_When_Some_Files_Are_Annotated()
    {
        FileChange[] files =
        [
            new("/a.cs", FileChangeKind.Edit),
            new("/b.cs", FileChangeKind.Edit),
        ];
        var annotations = new Dictionary<string, FileAnnotation>
        {
            ["/a.cs"] = new(Additions: 1, Deletions: 0, Viewed: true, HasUnresolved: false),
        };

        var rows = FileTree.Flatten(files, None, annotations);

        var b = Assert.Single(rows, r => r.Label == "b.cs");
        Assert.Null(b.Additions);
        Assert.Null(b.Deletions);
        Assert.False(b.Viewed);
        Assert.False(b.HasUnresolved);
    }

    [Fact]
    public void Annotations_Do_Not_Apply_To_Directory_Rows()
    {
        FileChange[] files =
        [
            new("/src/Web/Home.cs", FileChangeKind.Edit),
            new("/src/Web/About.cs", FileChangeKind.Edit),
        ];
        var annotations = new Dictionary<string, FileAnnotation>
        {
            ["/src/Web"] = new(Additions: 99, Deletions: 99, Viewed: true, HasUnresolved: true),
        };

        var rows = FileTree.Flatten(files, None, annotations);

        var dir = Assert.Single(rows, r => r.Kind == FileTreeRowKind.Directory);
        Assert.Null(dir.Additions);
        Assert.Null(dir.Deletions);
        Assert.False(dir.Viewed);
        Assert.False(dir.HasUnresolved);
    }
}
