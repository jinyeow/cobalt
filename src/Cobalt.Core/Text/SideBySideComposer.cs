using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Text;

/// <summary>
/// One row of the side-by-side view: the index of the diff line shown on the left
/// (old) and right (new) side, or null where that side is blank. Indices point back
/// into the original unified <see cref="DiffLine"/> list so the dialog can anchor a
/// comment to the correct line and side.
/// </summary>
public sealed record SideBySideRow(int? LeftIndex, int? RightIndex);

/// <summary>
/// Pure projection of a unified diff into a two-column (side-by-side) view (Item 3).
/// <see cref="Pair"/> turns the unified <see cref="DiffLine"/> list into aligned
/// left/right rows using the same k-th-removed↔k-th-added rule as
/// <see cref="DiffService"/> (so split rows line up with the intra-line emphasis it
/// already filled); <see cref="Compose"/> lays each row out as a
/// <see cref="StyledLine"/> the existing <c>DiffListDataSource</c> renders unchanged.
/// No Terminal.Gui types (ADR 0004).
/// </summary>
public static class SideBySideComposer
{
    public static IReadOnlyList<SideBySideRow> Pair(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind == DiffLineKind.Context)
            {
                rows.Add(new SideBySideRow(i, i));
                i++;
                continue;
            }

            // A maximal run of removed lines, then the run of added lines that follows
            // it — paired k-th↔k-th, with any surplus on one side rendered blank on the
            // other. Mirrors DiffService.FillIntraLineSpans exactly.
            var removedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed)
            {
                i++;
            }
            var removedCount = i - removedStart;

            var addedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added)
            {
                i++;
            }
            var addedCount = i - addedStart;

            var pairs = Math.Min(removedCount, addedCount);
            for (var k = 0; k < pairs; k++)
            {
                rows.Add(new SideBySideRow(removedStart + k, addedStart + k));
            }
            for (var k = pairs; k < removedCount; k++)
            {
                rows.Add(new SideBySideRow(removedStart + k, null));
            }
            for (var k = pairs; k < addedCount; k++)
            {
                rows.Add(new SideBySideRow(null, addedStart + k));
            }
        }
        return rows;
    }

    /// <summary>The separator drawn between the two columns.</summary>
    public const string Separator = " │ ";

    /// <summary>
    /// Lays each paired row out as a <see cref="StyledLine"/>: a left column and a
    /// right column, each exactly <paramref name="columnWidth"/> wide (padded or
    /// clipped) so the columns stay aligned, joined by <see cref="Separator"/>. The
    /// left column carries the old line's number/text (removed tint), the right the
    /// new line's (added tint); a blank side is neutral. Syntax + intra-line emphasis
    /// come from the shared <see cref="DiffLineStyler.AppendCodeRuns"/> path.
    /// </summary>
    public static IReadOnlyList<StyledLine> Compose(
        IReadOnlyList<DiffLine> lines,
        IReadOnlyList<SideBySideRow> rows,
        Language language,
        Func<DiffLine, bool> hasThread,
        int columnWidth)
    {
        var width = Math.Max(1, columnWidth);
        var result = new List<StyledLine>(rows.Count);
        foreach (var row in rows)
        {
            var left = BuildColumn(LineAt(lines, row.LeftIndex), left: true, language, hasThread, width);
            var right = BuildColumn(LineAt(lines, row.RightIndex), left: false, language, hasThread, width);

            var runs = new List<StyledRun>(left.Runs.Count + right.Runs.Count + 1);
            runs.AddRange(left.Runs);
            runs.Add(new StyledRun(
                width, Separator.Length, new RunStyle(TokenKind.Plain, DiffLineKind.Context, Emphasis: false, IsGutter: true)));
            var shift = width + Separator.Length;
            foreach (var r in right.Runs)
            {
                runs.Add(r with { Start = r.Start + shift });
            }
            result.Add(new StyledLine(left.Text + Separator + right.Text, runs));
        }
        return result;
    }

    private static DiffLine? LineAt(IReadOnlyList<DiffLine> lines, int? index) =>
        index is { } i ? lines[i] : null;

    private static (string Text, List<StyledRun> Runs) BuildColumn(
        DiffLine? line, bool left, Language language, Func<DiffLine, bool> hasThread, int width)
    {
        if (line is null)
        {
            // A blank side: neutral fill so the other column's tint doesn't bleed across.
            List<StyledRun> blank = [new(0, width, new RunStyle(TokenKind.Plain, DiffLineKind.Context, Emphasis: false, IsGutter: false))];
            return (new string(' ', width), blank);
        }

        var lineNumber = left ? line.OldLineNumber : line.NewLineNumber;
        var marker = hasThread(line) ? "●" : " ";
        var gutter = $"{marker}{lineNumber?.ToString() ?? "",4} ";
        var runs = new List<StyledRun>
        {
            new(0, gutter.Length, new RunStyle(TokenKind.Plain, line.Kind, Emphasis: false, IsGutter: true)),
        };
        DiffLineStyler.AppendCodeRuns(
            runs, SyntaxTokenizer.Tokenize(line.Text, language), line.ChangedSpans ?? [], line.Kind, gutter.Length);
        return Fit(gutter + line.Text, runs, width, line.Kind);
    }

    /// <summary>Pad the column with the line-kind background, or clip it, to exactly <paramref name="width"/>.</summary>
    private static (string Text, List<StyledRun> Runs) Fit(
        string text, List<StyledRun> runs, int width, DiffLineKind kind)
    {
        if (text.Length == width)
        {
            return (text, runs);
        }
        if (text.Length < width)
        {
            runs.Add(new StyledRun(
                text.Length, width - text.Length, new RunStyle(TokenKind.Plain, kind, Emphasis: false, IsGutter: false)));
            return (text + new string(' ', width - text.Length), runs);
        }

        var clipped = new List<StyledRun>(runs.Count);
        foreach (var r in runs)
        {
            if (r.Start >= width)
            {
                break;
            }
            var length = Math.Min(r.Length, width - r.Start);
            clipped.Add(length == r.Length ? r : r with { Length = length });
        }
        return (text[..width], clipped);
    }
}
