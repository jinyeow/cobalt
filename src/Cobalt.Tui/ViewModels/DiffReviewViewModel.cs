using Cobalt.Core.Models;
using Cobalt.Core.Text;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// What the diff dialog decides, minus the widgets: the state and pure transitions behind the
/// review pane, unit-testable without a terminal (ADR 0004). Dialog-owned, like
/// <see cref="DiffPaneComposer"/> — the screen constructs one and keeps it, so no test's dialog
/// construction changes.
///
/// <para><b>Identity — the rule this class is built around.</b> The file under the tree cursor and
/// the file on screen are different things during a select: <c>PrDiffViewModel.SelectFileAsync</c>
/// moves <c>SelectedFile</c> synchronously and only publishes the fetched diff when it lands, so
/// in that window the cursor has already moved on while the previous file is still displayed. Four
/// shipped bugs came from confusing them. Everything this class owns is <b>DISPLAYED</b>-side —
/// derived from <c>CurrentDiff</c> / <c>CurrentDiffSnapshot</c> — and it must never read
/// <c>SelectedFile</c>. The <b>CURSOR</b> half stays in the dialog, which owns the async select
/// that writes it.</para>
/// </summary>
internal sealed class DiffReviewViewModel(PrDiffViewModel vm)
{
    private string? _searchQuery;
    private IReadOnlyList<(int LineIndex, LineSpan Span)> _searchMatches = [];
    private int _searchIndex;
    private readonly DiffPaneComposer _composer = new();
    // DISPLAYED: the path of the last snapshot composed, which is what "same file" means here —
    // never the tree cursor's file.
    private string? _renderedDiffPath;
    private bool _sideBySide;
    private DiffFoldState? _foldState;
    private IReadOnlyList<DiffRow> _diffRows = [];
    // unified line index → the first _diffRows index showing it (any of LineIndex/LeftIndex/
    // RightIndex), rebuilt with _diffRows so IsLineVisible / RowForLine are O(1) rather than
    // an O(rows) scan on every n/N, hunk/thread nav and search hop (RENDER-7).
    private IReadOnlyDictionary<int, int> _lineToRow = new Dictionary<int, int>();

    /// <summary>The active search's matches, as unified line indexes into the DISPLAYED diff.</summary>
    internal IReadOnlyList<(int LineIndex, LineSpan Span)> Matches => _searchMatches;

    /// <summary>The number of matches for the active search (0 when no search is active).</summary>
    internal int MatchCount => _searchMatches.Count;

    /// <summary>
    /// The result of applying a search: at most one of the two is set. <paramref name="JumpToLine"/>
    /// is the unified line the caller should reveal and select; <paramref name="NoMatchesFor"/> is
    /// the (trimmed) query the caller should report as unmatched. Both null means the search was
    /// cleared, which is silent. They are separate fields because "no matches" must not be
    /// expressible as a jump to line 0 — that would quietly move the cursor to the top of the file.
    /// </summary>
    internal readonly record struct SearchOutcome(int? JumpToLine, string? NoMatchesFor);

    /// <summary>
    /// Runs a query against the DISPLAYED diff, parking the match index on the first hit. A blank
    /// or whitespace-only query — or no diff on screen — clears the search instead.
    /// </summary>
    internal SearchOutcome ApplySearch(string? query)
    {
        if (vm.CurrentDiff is not { } diff || string.IsNullOrWhiteSpace(query))
        {
            ClearSearch();
            return new SearchOutcome(null, null);
        }
        _searchQuery = query.Trim();
        _searchMatches = DiffSearch.Find(diff.Lines, _searchQuery);
        _searchIndex = 0;
        return _searchMatches.Count > 0
            ? new SearchOutcome(_searchMatches[_searchIndex].LineIndex, null)
            : new SearchOutcome(null, _searchQuery);
    }

    /// <summary>
    /// n/N: advance to the next/previous match, wrapping. Returns the unified line to reveal, or
    /// null when there is no active search — so the caller stays put rather than selecting line 0.
    /// </summary>
    internal int? StepSearch(bool forward)
    {
        if (_searchMatches.Count == 0)
        {
            return null;
        }
        _searchIndex = forward
            ? DiffSearch.Next(_searchMatches, _searchIndex)
            : DiffSearch.Prev(_searchMatches, _searchIndex);
        return _searchMatches[_searchIndex].LineIndex;
    }

    /// <summary>
    /// Drops the active search. Called when the query is blank and when the DISPLAYED file changes:
    /// matches are line-index scoped to one file, so carrying them across would decorate the new
    /// file at the old file's lines.
    /// </summary>
    internal void ClearSearch()
    {
        _searchQuery = null;
        _searchMatches = [];
    }

    /// <summary>Whether the diff pane is in side-by-side mode. Pane mode, not a per-file property.</summary>
    internal bool SideBySide => _sideBySide;

    /// <summary>The single row→line map every diff-pane consumer (comment, thread, nav, search) goes through.</summary>
    internal IReadOnlyList<DiffRow> DiffRows => _diffRows;

    /// <summary>The side-by-side row map, projected from the unified row map (empty in unified mode).</summary>
    internal IReadOnlyList<SideBySideRow> SideBySideRows =>
        _sideBySide ? [.. _diffRows.Select(r => new SideBySideRow(r.LeftIndex, r.RightIndex))] : [];

    /// <summary>
    /// The unified diff line the pane cursor points at, in either mode (new side preferred).
    /// Returns -1 for an anchorless row (a fold marker) so comment/thread guards bail cleanly
    /// rather than anchoring to line 0.
    /// </summary>
    internal int LineAtRow(int? paneSelectedRow)
    {
        var sel = paneSelectedRow ?? 0;
        if (sel >= 0 && sel < _diffRows.Count)
        {
            return _diffRows[sel].Anchor ?? -1;
        }
        return -1;
    }

    /// <summary>
    /// The unified line nearest the cursor, for navigation that has to start somewhere: scans
    /// forward off a fold marker, then backward. Unlike <see cref="LineAtRow"/> this never refuses
    /// — ]c/[c and ]t/[t need a line to move *from*, where a comment needs a line to attach *to*.
    /// </summary>
    internal int NearestLineAtRow(int? paneSelectedRow)
    {
        var sel = paneSelectedRow ?? 0;
        for (var i = sel; i < _diffRows.Count; i++)
        {
            if (_diffRows[i].Anchor is { } a)
            {
                return a;
            }
        }
        for (var i = sel - 1; i >= 0; i--)
        {
            if (_diffRows[i].Anchor is { } a)
            {
                return a;
            }
        }
        return 0;
    }

    /// <summary>Whether a unified line currently has a row on screen (false when a fold hides it).</summary>
    internal bool IsLineVisible(int lineIndex) => _lineToRow.ContainsKey(lineIndex);

    /// <summary>The pane row showing a unified line, or 0 when it has none.</summary>
    internal int RowForLine(int lineIndex) => _lineToRow.TryGetValue(lineIndex, out var row) ? row : 0;

    /// <summary>
    /// What the diff pane should now show: <paramref name="Styled"/> becomes its source, and
    /// <paramref name="SelectedRow"/> the cursor row — null when there is nothing to select, so the
    /// caller skips the write rather than selecting row 0 of an empty pane.
    /// </summary>
    internal readonly record struct PaneUpdate(IReadOnlyList<StyledLine> Styled, int? SelectedRow);

    /// <summary>
    /// Recomposes the diff pane for the DISPLAYED snapshot. Same-file refreshes (a comment landing,
    /// a mark-viewed) keep the reviewer's line and their e/E fold expansions; a genuine file change
    /// resets to the top, rebuilds the folds, and drops the search, whose match line-indexes belong
    /// to the file that just left the screen.
    /// </summary>
    /// <param name="snapshot">
    /// The diff and its own path, read atomically by the caller (ADR 0008) — pairing them prevents
    /// showing one file's lines under another's path when a select is in flight.
    /// </param>
    /// <param name="contentWidth">The diff pane's usable width, which drives the side-by-side columns.</param>
    /// <param name="paneSelectedRow">The pane's current cursor row, retained only on a same-file refresh.</param>
    internal PaneUpdate ComposePane((FileDiff Diff, string Path) snapshot, int contentWidth, int? paneSelectedRow)
    {
        var sameFile = snapshot.Path == _renderedDiffPath;
        var keepLine = sameFile ? paneSelectedRow : 0;
        if (!sameFile)
        {
            // Search matches are line-index scoped to one file; drop them when the file changes.
            ClearSearch();
        }
        _renderedDiffPath = snapshot.Path;

        // All mode/fold/search branching lives in the pure composer (ADR 0004). This decides only
        // fold reuse — pass the retained state on a same-file refresh so e/E expansions survive
        // (thread markers, mark-viewed), or null to rebuild — and stores what it returns.
        var (commentedLeft, commentedRight) = vm.CommentedLinesFor(snapshot.Path);
        var composition = _composer.Compose(new DiffPaneRequest(
            snapshot.Diff, snapshot.Path, _sideBySide, sameFile ? _foldState : null,
            _searchMatches, contentWidth, commentedLeft, commentedRight));
        _foldState = composition.FoldState;
        _diffRows = composition.Rows;
        _lineToRow = composition.LineToRow;
        return new PaneUpdate(
            composition.Styled,
            composition.Styled.Count > 0 ? Math.Clamp(keepLine ?? 0, 0, composition.Styled.Count - 1) : null);
    }

    /// <summary>
    /// Drops the pane to unified because the dialog is too narrow for two columns. Deliberately
    /// one-way rather than a setter: widening again does not restore side-by-side, only the user's
    /// 's' does — the force is sticky, and a setter would invite silently un-forcing it.
    /// </summary>
    internal void ForceUnified() => _sideBySide = false;

    /// <summary>s: flip the pane mode, returning the mode it landed in (what the caller reports).</summary>
    internal bool ToggleMode() => _sideBySide = !_sideBySide;

    /// <summary>
    /// e: expand the fold whose marker sits at <paramref name="paneSelectedRow"/>, or the first
    /// collapsed fold if the cursor is elsewhere. Returns whether the pane needs recomposing —
    /// false in side-by-side (which carries no folds) and when nothing collapsed remains.
    /// </summary>
    internal bool ExpandFoldAt(int? paneSelectedRow)
    {
        if (_sideBySide || _foldState is null)
        {
            return false;
        }
        var sel = paneSelectedRow ?? -1;
        var foldId = sel >= 0 && sel < _diffRows.Count ? _diffRows[sel].FoldId : null;
        foldId ??= _diffRows.FirstOrDefault(r => r.FoldId is not null)?.FoldId;
        if (foldId is { } id)
        {
            _foldState = _foldState.Expand(id);
            return true;
        }
        return false;
    }

    /// <summary>E: expand every fold in the displayed file. Returns whether the pane needs recomposing.</summary>
    internal bool ExpandAllFolds()
    {
        if (_sideBySide || _foldState is null)
        {
            return false;
        }
        _foldState = _foldState.ExpandAll();
        return true;
    }

    /// <summary>
    /// Makes a unified line reachable before the caller selects it (n/N, hunk and thread nav all
    /// land on lines a fold may hide). Returns whether the fold state changed and so the pane needs
    /// recomposing; a line that is already on screen needs no re-render.
    /// </summary>
    internal bool RevealLine(int lineIndex)
    {
        if (!_sideBySide && _foldState is not null && !IsLineVisible(lineIndex))
        {
            _foldState = _foldState.ExpandContaining(lineIndex);
            return true;
        }
        return false;
    }

    // ---- file tree ---------------------------------------------------------------------------
    //
    // NEITHER identity: the tree is keyed by node path and built from vm.Files, independent of both
    // the cursor and the displayed diff. The navigation methods take the current path as a
    // parameter rather than reading it, which is what keeps the CURSOR half out of this class.

    private readonly HashSet<string> _collapsedDirs = new(StringComparer.Ordinal);
    private IReadOnlyList<FileTreeRow> _rows = [];
    private List<string> _fileListStrings = [];

    /// <summary>The flattened file-tree rows currently shown in the file list.</summary>
    internal IReadOnlyList<FileTreeRow> Rows => _rows;

    /// <summary>
    /// What the file list should now show. <paramref name="Strings"/> is non-null only when the
    /// rendered rows actually changed, because assigning a new source nulls the widget's selection;
    /// <paramref name="TargetRow"/> is the row to highlight, or null to leave the caller's own
    /// selection in place — which the caller must read <i>after</i> applying
    /// <paramref name="Strings"/>, clamped to <see cref="Rows"/>.Count.
    /// </summary>
    internal readonly record struct TreeUpdate(IReadOnlyList<string>? Strings, int? TargetRow);

    /// <summary>
    /// Re-flattens the changed files into the directory tree and resolves the row to highlight.
    /// Under the unresolved-only filter the tree is built from the filtered projection; leaves
    /// still carry their real path as NodePath, so opening one resolves against vm.Files by path.
    ///
    /// <para><b>Contract the caller must honour:</b> when <c>Strings</c> comes back non-null the
    /// caller <i>must</i> apply it to the list. This method records what it handed out in order to
    /// answer "did the rows change" next time, so a caller that skips the assignment leaves that
    /// record describing a list the widget never received — and because every later rebuild then
    /// compares equal, the file list would stop updating permanently rather than visibly break.</para>
    /// </summary>
    internal TreeUpdate RebuildTree(string? selectNodePath)
    {
        var files = vm.OnlyUnresolvedFiles ? vm.FilteredFiles : vm.Files;
        _rows = FileTree.Flatten(files, _collapsedDirs, BuildAnnotations());
        var strings = _rows.Select(FormatRow).ToList();
        IReadOnlyList<string>? changed = null;
        if (!strings.SequenceEqual(_fileListStrings, StringComparer.Ordinal))
        {
            _fileListStrings = strings;
            changed = strings;
        }
        if (_rows.Count == 0)
        {
            return new TreeUpdate(changed, null);
        }
        var target = selectNodePath is null ? -1 : IndexOfNode(selectNodePath);
        return new TreeUpdate(changed, target >= 0 ? target : null);
    }

    /// <summary>Collapse or expand a directory node.</summary>
    internal void ToggleDir(string nodePath)
    {
        if (!_collapsedDirs.Remove(nodePath))
        {
            _collapsedDirs.Add(nodePath);
        }
    }

    /// <summary>
    /// z: collapse/expand the folder under the cursor, or collapse the nearest ancestor folder of a
    /// file row. Returns the node to re-highlight, or null when the row means nothing. The file-row
    /// case always collapses and never expands — "fold up what I am inside" — so pressing z twice
    /// on a file does not re-open the folder the first press closed.
    /// </summary>
    internal string? ToggleDirAt(int? rowIndex)
    {
        var sel = rowIndex ?? -1;
        if (sel < 0 || sel >= _rows.Count)
        {
            return null;
        }
        var row = _rows[sel];
        if (row.Kind == FileTreeRowKind.Directory)
        {
            ToggleDir(row.NodePath);
            return row.NodePath;
        }
        if (NearestAncestorDir(sel) is not { } parent)
        {
            return null;
        }
        _collapsedDirs.Add(parent.NodePath);
        return parent.NodePath;
    }

    /// <summary>
    /// [f/]f: the vm.Files index of the previous/next file among the visible leaves, skipping
    /// folder rows and clamping at the ends. Null when the tree has no leaves at all; otherwise the
    /// raw lookup, which the caller's select clamps exactly as it always has.
    /// </summary>
    internal int? StepFileTarget(string? currentPath, int delta)
    {
        var fileRows = FileRows();
        if (fileRows.Count == 0)
        {
            return null;
        }
        var current = IndexOfLeaf(fileRows, currentPath);
        var next = Math.Clamp((current < 0 ? 0 : current) + delta, 0, fileRows.Count - 1);
        return FileIndexForPath(fileRows[next].NodePath);
    }

    /// <summary>
    /// [v/]v: the vm.Files index of the nearest file in that direction not yet marked viewed, or
    /// null when there is none — the cursor then stays put rather than jumping to an end.
    /// </summary>
    internal int? NextUnviewedTarget(string? currentPath, int delta)
    {
        var fileRows = FileRows();
        if (fileRows.Count == 0)
        {
            return null;
        }
        var current = IndexOfLeaf(fileRows, currentPath);
        for (var i = (current < 0 ? 0 : current) + delta; i >= 0 && i < fileRows.Count; i += delta)
        {
            if (!vm.IsViewed(fileRows[i].NodePath))
            {
                return FileIndexForPath(fileRows[i].NodePath);
            }
        }
        return null;
    }

    /// <summary>The vm.Files index of a path, or -1. Rows carry filtered-relative indexes, so
    /// opening one must resolve by path to keep the diff pane and the cursor on one identity.</summary>
    internal int FileIndexForPath(string path)
    {
        for (var i = 0; i < vm.Files.Count; i++)
        {
            if (string.Equals(vm.Files[i].Path, path, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The index of a node among the current rows, or -1.</summary>
    private int IndexOfNode(string nodePath)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].NodePath, nodePath, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private List<FileTreeRow> FileRows() => [.. _rows.Where(r => r.FileIndex is not null)];

    private static int IndexOfLeaf(List<FileTreeRow> fileRows, string? path) =>
        fileRows.FindIndex(r => string.Equals(r.NodePath, path, StringComparison.Ordinal));

    private FileTreeRow? NearestAncestorDir(int rowIndex)
    {
        var depth = _rows[rowIndex].Depth;
        for (var i = rowIndex - 1; i >= 0; i--)
        {
            if (_rows[i].Kind == FileTreeRowKind.Directory && _rows[i].Depth < depth)
            {
                return _rows[i];
            }
        }
        return null;
    }

    /// <summary>Per-file review metadata (diff stat, viewed, unresolved) keyed by path for the tree.</summary>
    private IReadOnlyDictionary<string, FileAnnotation> BuildAnnotations()
    {
        // The unresolved-file set is recomputed once per Threads write in the view-model
        // (HarvestThreadsAsync), so a file's "has unresolved comments" dot is an O(1) lookup
        // here instead of scanning every thread per file on each file-tree rebuild (RENDER-2).
        var unresolved = vm.UnresolvedFilePaths;
        var map = new Dictionary<string, FileAnnotation>(StringComparer.Ordinal);
        foreach (var file in vm.Files)
        {
            var stats = vm.StatsFor(file.Path);
            map[file.Path] = new FileAnnotation(
                stats?.Additions, stats?.Deletions, vm.IsViewed(file.Path), unresolved.Contains(file.Path));
        }
        return map;
    }

    private static string FormatRow(FileTreeRow row)
    {
        var indent = new string(' ', row.Depth * 2);
        if (row.Kind == FileTreeRowKind.Directory)
        {
            return $"{indent}{(row.Collapsed ? "▸" : "▾")} {row.Label}/";
        }
        var glyph = row.ChangeType switch
        {
            FileChangeKind.Add => "+",
            FileChangeKind.Delete => "-",
            FileChangeKind.Rename => "»",
            _ => "~",
        };
        var viewed = row.Viewed ? "[✓] " : "[ ] ";
        var unresolved = row.HasUnresolved ? " ●" : "";
        var stats = row.Additions is { } a && row.Deletions is { } d ? $"  +{a} -{d}" : "";
        return $"{indent}{viewed}{glyph} {row.Label}{unresolved}{stats}";
    }
}
