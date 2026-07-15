using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Text;

/// <summary>
/// Caches one file's composed <see cref="StyledLine"/>s across diff-pane renders. Composition
/// (tokenize + <see cref="DiffLineStyler.Compose"/>) costs O(line length) per line, and the pane
/// re-renders for reasons that change no line at all — expanding a fold, toggling the file filter,
/// applying a search, a comment round-trip — so recomposing the file on each one is the single
/// biggest per-keystroke cost in review. <see cref="Prepare"/> is called once per render with the
/// render's inputs; anything already composed from the same inputs is returned as-is.
///
/// <para>The two modes keep separate caches: unified composes lazily per line, because folds change
/// which lines are visible from one render to the next, while side-by-side composes every row in
/// one pass and so is cached whole. Nothing here resolves a colour — a <see cref="StyledLine"/>
/// carries roles and diff kinds only, and the theme is applied at paint (ADR 0019), so a cached
/// line recolours live with <c>:theme</c>.</para>
///
/// <para>Search hits are deliberately not part of the cache: they are an overlay the caller applies
/// to the composed line (<see cref="DiffLineStyler.WithSearchHits"/>), so a query change never
/// invalidates anything. Pure; no Terminal.Gui types (ADR 0004).</para>
/// </summary>
public sealed class DiffStyleCache
{
    private IReadOnlyList<DiffLine> _lines = [];
    private Language _language;
    private IReadOnlySet<int> _commentedLeft = new HashSet<int>();
    private IReadOnlySet<int> _commentedRight = new HashSet<int>();

    /// <summary>A composed line plus the marker state it was composed with, so a moved marker is detectable.</summary>
    private sealed record Entry(StyledLine Line, bool HasThread);

    private Entry?[] _unified = [];
    private IReadOnlyList<StyledLine>? _sideBySide;
    private int _sideBySideWidth = -1;

    /// <summary>
    /// Points the cache at the file about to be rendered and the line numbers carrying comment
    /// threads on each side (as <c>PrDiffViewModel.CommentedLinesFor</c> reports them: left for
    /// removed lines, right for added and context). Drops every composition whose inputs changed
    /// — a different file or language drops all of them, a changed thread marker drops just the
    /// lines it moved. Cheap enough to call on every render.
    /// </summary>
    public void Prepare(
        IReadOnlyList<DiffLine> lines,
        Language language,
        IReadOnlySet<int> commentedLeft,
        IReadOnlySet<int> commentedRight)
    {
        // The view model caches a FileDiff per path and never evicts it, so the same file yields
        // the same list instance on every render — identity is the file's identity here.
        if (!ReferenceEquals(lines, _lines) || language != _language)
        {
            _lines = lines;
            _language = language;
            _unified = new Entry?[lines.Count];
            _sideBySide = null;
        }
        else if (!_commentedLeft.SetEquals(commentedLeft) || !_commentedRight.SetEquals(commentedRight))
        {
            // The sets are rebuilt on every render, so only a change in *contents* counts. Unified
            // drops per line (in Unified, by comparing the marker each line was composed with);
            // side-by-side composes in one pass and so goes whole.
            _sideBySide = null;
        }
        _commentedLeft = commentedLeft;
        _commentedRight = commentedRight;
    }

    /// <summary>The composed unified line at <paramref name="lineIndex"/>, recomposed only if its inputs changed.</summary>
    public StyledLine Unified(int lineIndex)
    {
        var line = _lines[lineIndex];
        var hasThread = HasThread(line);
        if (_unified[lineIndex] is { } cached && cached.HasThread == hasThread)
        {
            return cached.Line;
        }
        var composed = DiffLineStyler.Compose(line, SyntaxTokenizer.Tokenize(line.Text, _language), hasThread);
        _unified[lineIndex] = new Entry(composed, hasThread);
        return composed;
    }

    /// <summary>
    /// The composed side-by-side rows at <paramref name="columnWidth"/>, reusing the previous
    /// composition unless the width or a thread marker changed. <paramref name="rows"/> is a pure
    /// projection of the prepared lines (<see cref="SideBySideComposer.Pair"/>), so it is not part
    /// of the key.
    /// </summary>
    public IReadOnlyList<StyledLine> SideBySide(IReadOnlyList<SideBySideRow> rows, int columnWidth)
    {
        if (_sideBySide is { } cached && _sideBySideWidth == columnWidth)
        {
            return cached;
        }
        _sideBySideWidth = columnWidth;
        return _sideBySide = SideBySideComposer.Compose(_lines, rows, _language, HasThread, columnWidth);
    }

    private bool HasThread(DiffLine line) => DiffThreadAnchor.HasThread(line, _commentedLeft, _commentedRight);
}

/// <summary>
/// Where a review comment anchors on a diff line: the right (new) side for added and context
/// lines, the left (old) side for removed ones — the rule Azure DevOps files comments under.
/// Shared so the marker a reviewer sees and the line <c>]t</c>/<c>[t</c> jumps to are decided by
/// one rule; two copies that drifted would navigate to lines showing no marker.
/// </summary>
public static class DiffThreadAnchor
{
    /// <summary>
    /// Whether <paramref name="line"/> carries a thread, given the commented line numbers on each
    /// side (as <c>PrDiffViewModel.CommentedLinesFor</c> reports them).
    /// </summary>
    public static bool HasThread(DiffLine line, IReadOnlySet<int> commentedLeft, IReadOnlySet<int> commentedRight) =>
        line.Kind == DiffLineKind.Removed
            ? line.OldLineNumber is { } l && commentedLeft.Contains(l)
            : line.NewLineNumber is { } r && commentedRight.Contains(r);
}
