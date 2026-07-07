using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

public enum FileTreeRowKind
{
    Directory,
    File,
}

/// <summary>
/// One visible row of the diff-review file list: a directory header or a file
/// leaf. <see cref="FileIndex"/> maps a leaf back to its entry in the original
/// changed-file list (null on directories), so navigation lands on files and
/// skips headers. <see cref="NodePath"/> is a stable key used to toggle a
/// directory's collapsed state.
/// </summary>
public sealed record FileTreeRow(
    FileTreeRowKind Kind,
    int Depth,
    string Label,
    FileChangeKind? ChangeType,
    int? FileIndex,
    string NodePath,
    bool Collapsed,
    int? Additions = null,
    int? Deletions = null,
    bool Viewed = false,
    bool HasUnresolved = false);

/// <summary>Per-file review metadata (diff stat + review state) overlaid onto a leaf's <see cref="FileTreeRow"/>.</summary>
public sealed record FileAnnotation(
    int? Additions = null,
    int? Deletions = null,
    bool Viewed = false,
    bool HasUnresolved = false);

/// <summary>
/// Pure projection of a PR's changed files into a directory tree of display rows
/// (ADR 0004 — no Terminal.Gui types, unit-tested). Directories are grouped and
/// sorted, single-child directory chains are compressed (<c>src/Web</c>), and the
/// distinguishing basename is always shown in full. Flattening honors a set of
/// collapsed directory paths so the dialog can hold collapse state and re-flatten.
/// </summary>
public static class FileTree
{
    private sealed class DirNode
    {
        public SortedDictionary<string, DirNode> Subdirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<FileLeaf> Files { get; } = [];

        public DirNode Child(string name)
        {
            if (!Subdirs.TryGetValue(name, out var child))
            {
                Subdirs[name] = child = new DirNode();
            }
            return child;
        }
    }

    private sealed record FileLeaf(string Name, string Path, int Index, FileChangeKind ChangeType);

    public static IReadOnlyList<FileTreeRow> Flatten(
        IReadOnlyList<FileChange> files,
        IReadOnlySet<string> collapsedDirs,
        IReadOnlyDictionary<string, FileAnnotation>? annotations = null)
    {
        var root = new DirNode();
        for (var i = 0; i < files.Count; i++)
        {
            var (dirs, name) = SplitPath(files[i].Path);
            var node = root;
            foreach (var dir in dirs)
            {
                node = node.Child(dir);
            }
            node.Files.Add(new FileLeaf(name, files[i].Path, i, files[i].ChangeType));
        }

        var rows = new List<FileTreeRow>();
        Emit(root, "", 0, collapsedDirs, rows, annotations);
        return rows;
    }

    private static void Emit(
        DirNode node,
        string path,
        int depth,
        IReadOnlySet<string> collapsed,
        List<FileTreeRow> rows,
        IReadOnlyDictionary<string, FileAnnotation>? annotations)
    {
        foreach (var (segment, child) in node.Subdirs)
        {
            // Compress a single-child directory chain into one row (src/Web/Api),
            // so the tree doesn't waste a line per intermediate directory.
            var label = segment;
            var nodePath = path + "/" + segment;
            var current = child;
            while (current.Subdirs.Count == 1 && current.Files.Count == 0)
            {
                var (onlyName, onlyChild) = current.Subdirs.First();
                label += "/" + onlyName;
                nodePath += "/" + onlyName;
                current = onlyChild;
            }

            var isCollapsed = collapsed.Contains(nodePath);
            rows.Add(new FileTreeRow(FileTreeRowKind.Directory, depth, label, null, null, nodePath, isCollapsed));
            if (!isCollapsed)
            {
                Emit(current, nodePath, depth + 1, collapsed, rows, annotations);
            }
        }

        foreach (var leaf in node.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var annotation = annotations is not null && annotations.TryGetValue(leaf.Path, out var found) ? found : null;
            rows.Add(new FileTreeRow(
                FileTreeRowKind.File, depth, leaf.Name, leaf.ChangeType, leaf.Index, leaf.Path, Collapsed: false,
                Additions: annotation?.Additions, Deletions: annotation?.Deletions,
                Viewed: annotation?.Viewed ?? false, HasUnresolved: annotation?.HasUnresolved ?? false));
        }
    }

    private static (IReadOnlyList<string> Dirs, string Name) SplitPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return ([], path);
        }
        return (segments[..^1], segments[^1]);
    }
}
