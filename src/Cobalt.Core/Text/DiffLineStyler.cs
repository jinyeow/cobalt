using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Text;

/// <summary>Foreground = syntax token; background = diff state (+ emphasis for changed words).</summary>
public sealed record RunStyle(TokenKind Token, DiffLineKind LineKind, bool Emphasis, bool IsGutter);

/// <summary>A styled slice of <see cref="StyledLine.DisplayText"/>: [Start, Start+Length).</summary>
public sealed record StyledRun(int Start, int Length, RunStyle Style);

/// <summary>A fully-composed diff line: the display string plus a gapless, ordered run partition.</summary>
public sealed record StyledLine(string DisplayText, IReadOnlyList<StyledRun> Runs);

/// <summary>
/// Pure compositor turning a <see cref="DiffLine"/> + its syntax tokens + thread
/// flag into a display string and a set of styled runs (SPEC §3 review). The
/// display string reproduces the legacy DiffReviewDialog.FormatLine output
/// exactly. Runs partition the display string with no gaps or overlaps: the
/// gutter is one run; code runs are the syntax tokens, split at every
/// <see cref="DiffLine.ChangedSpans"/> boundary so emphasis lands exactly on the
/// changed words while keeping each token's kind (layering rule, ADR 0010).
/// </summary>
public static class DiffLineStyler
{
    public static StyledLine Compose(DiffLine line, IReadOnlyList<SyntaxToken> tokens, bool hasThread)
    {
        var marker = hasThread ? "●" : " ";
        var sign = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "-",
            _ => " ",
        };
        var oldNo = line.OldLineNumber?.ToString() ?? "";
        var newNo = line.NewLineNumber?.ToString() ?? "";
        var prefix = $"{marker}{oldNo,4} {newNo,4} {sign}";
        var displayText = prefix + line.Text;
        var gutterLen = prefix.Length;

        var runs = new List<StyledRun>
        {
            // Whole gutter as a single run; carries the line kind so the adapter
            // can tint the sign/line numbers by diff state.
            new(0, gutterLen, new RunStyle(TokenKind.Plain, line.Kind, Emphasis: false, IsGutter: true)),
        };

        AppendCodeRuns(runs, tokens, line.ChangedSpans ?? [], line.Kind, gutterLen);
        return new StyledLine(displayText, runs);
    }

    /// <summary>
    /// Appends the code runs for one text segment: each syntax token, split at every
    /// <paramref name="spans"/> boundary so emphasis lands exactly on the changed
    /// words while keeping the token's kind. Run offsets are shifted by
    /// <paramref name="offset"/> (the gutter width of the column the text sits in).
    /// Shared by the unified styler and the side-by-side composer so both partition
    /// code identically.
    /// </summary>
    internal static void AppendCodeRuns(
        List<StyledRun> runs,
        IReadOnlyList<SyntaxToken> tokens,
        IReadOnlyList<LineSpan> spans,
        DiffLineKind kind,
        int offset)
    {
        foreach (var token in tokens)
        {
            var pos = token.Start;
            var end = token.Start + token.Length;
            while (pos < end)
            {
                var inSpan = false;
                var boundary = end;
                foreach (var s in spans)
                {
                    var sStart = s.Start;
                    var sEnd = s.Start + s.Length;
                    if (sStart <= pos && pos < sEnd)
                    {
                        inSpan = true;
                        if (sEnd < boundary)
                        {
                            boundary = sEnd;
                        }
                    }
                    else if (sStart > pos && sStart < boundary)
                    {
                        boundary = sStart;
                    }
                }

                runs.Add(new StyledRun(
                    offset + pos,
                    boundary - pos,
                    new RunStyle(token.Kind, kind, Emphasis: inSpan, IsGutter: false)));
                pos = boundary;
            }
        }
    }
}
