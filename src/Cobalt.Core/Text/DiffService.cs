using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Cobalt.Core.Text;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

public sealed record FileDiff(
    IReadOnlyList<DiffLine> Lines,
    int Additions,
    int Deletions,
    bool IsBinary,
    bool TooLarge)
{
    public static FileDiff Binary() => new([], 0, 0, IsBinary: true, TooLarge: false);
    public static FileDiff Oversized() => new([], 0, 0, IsBinary: false, TooLarge: true);
}

/// <summary>Computes a unified line diff between two file versions (SPEC §3 review).</summary>
public static class DiffService
{
    private static readonly InlineDiffBuilder Builder = new(new Differ());

    public static FileDiff Unified(string? oldText, string? newText, int maxLines = 50_000)
    {
        oldText ??= "";
        newText ??= "";

        if (LooksBinary(oldText) || LooksBinary(newText))
        {
            return FileDiff.Binary();
        }
        if (CountLines(oldText) > maxLines || CountLines(newText) > maxLines)
        {
            return FileDiff.Oversized();
        }

        // Strip a single trailing newline per side so the final newline doesn't
        // register as a phantom empty line (git-style line semantics).
        var model = Builder.BuildDiffModel(TrimOneEol(oldText), TrimOneEol(newText), ignoreWhitespace: false);

        var lines = new List<DiffLine>(model.Lines.Count);
        var oldNo = 0;
        var newNo = 0;
        var additions = 0;
        var deletions = 0;

        foreach (var line in model.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    newNo++;
                    additions++;
                    lines.Add(new DiffLine(DiffLineKind.Added, null, newNo, line.Text));
                    break;
                case ChangeType.Deleted:
                    oldNo++;
                    deletions++;
                    lines.Add(new DiffLine(DiffLineKind.Removed, oldNo, null, line.Text));
                    break;
                case ChangeType.Modified:
                    // InlineDiffBuilder emits modified as separate delete+insert; treat defensively.
                    oldNo++;
                    newNo++;
                    lines.Add(new DiffLine(DiffLineKind.Context, oldNo, newNo, line.Text));
                    break;
                case ChangeType.Unchanged:
                case ChangeType.Imaginary:
                default:
                    oldNo++;
                    newNo++;
                    lines.Add(new DiffLine(DiffLineKind.Context, oldNo, newNo, line.Text));
                    break;
            }
        }

        return new FileDiff(lines, additions, deletions, IsBinary: false, TooLarge: false);
    }

    private static string TrimOneEol(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }
        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            return text[..^1];
        }
        return text;
    }

    private static bool LooksBinary(string text)
    {
        // A NUL byte in the first chunk is the common "this is binary" heuristic.
        var scan = Math.Min(text.Length, 8000);
        for (var i = 0; i < scan; i++)
        {
            if (text[i] == '\0')
            {
                return true;
            }
        }
        return false;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
