using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

public class DiffLineStylerTests
{
    // Mirrors DiffReviewDialog.FormatLine exactly — the rendering-compat spec.
    private static string ExpectedDisplay(DiffLine line, bool hasThread)
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
        return $"{marker}{oldNo,4} {newNo,4} {sign}{line.Text}";
    }

    private static StyledLine Compose(DiffLine line, bool hasThread, Language lang = Language.CSharp) =>
        DiffLineStyler.Compose(line, SyntaxTokenizer.Tokenize(line.Text, lang), hasThread);

    private static void AssertPartitions(StyledLine styled)
    {
        var pos = 0;
        foreach (var r in styled.Runs)
        {
            Assert.Equal(pos, r.Start);
            Assert.True(r.Length > 0);
            pos += r.Length;
        }
        Assert.Equal(styled.DisplayText.Length, pos);
    }

    [Theory]
    [InlineData(DiffLineKind.Context, 3, 5, "hello world", false)]
    [InlineData(DiffLineKind.Context, 3, 5, "hello world", true)]
    [InlineData(DiffLineKind.Added, null, 2, "var x = 1;", false)]
    [InlineData(DiffLineKind.Removed, 7, null, "var x = 0;", true)]
    public void DisplayText_Matches_FormatLine(DiffLineKind kind, int? oldNo, int? newNo, string text, bool thread)
    {
        var line = new DiffLine(kind, oldNo, newNo, text);
        var styled = Compose(line, thread);

        Assert.Equal(ExpectedDisplay(line, thread), styled.DisplayText);
    }

    [Fact]
    public void Runs_Partition_DisplayText_And_Gutter_Is_Flagged()
    {
        var line = new DiffLine(DiffLineKind.Added, null, 2, "var x = 1;");
        var styled = Compose(line, hasThread: false);

        AssertPartitions(styled);

        // The gutter is the prefix before the code text.
        var gutterLen = styled.DisplayText.Length - line.Text.Length;
        var firstCode = styled.Runs.First(r => !r.Style.IsGutter);
        Assert.Equal(gutterLen, firstCode.Start);
        Assert.All(styled.Runs.Where(r => r.Style.IsGutter), r => Assert.True(r.Start < gutterLen));
    }

    [Fact]
    public void All_Runs_Carry_The_Lines_DiffLineKind()
    {
        var line = new DiffLine(DiffLineKind.Removed, 7, null, "var x = 0;");
        var styled = Compose(line, hasThread: false);

        Assert.All(styled.Runs, r => Assert.Equal(DiffLineKind.Removed, r.Style.LineKind));
    }

    [Fact]
    public void Context_Lines_Never_Have_Emphasis()
    {
        var line = new DiffLine(DiffLineKind.Context, 1, 1, "var total = price;");
        var styled = Compose(line, hasThread: false);

        Assert.All(styled.Runs, r => Assert.False(r.Style.Emphasis));
    }

    [Fact]
    public void Token_Straddling_A_ChangedSpan_Is_Split_With_Emphasis_Keeping_Kind()
    {
        // "cost" is the changed word; the emphasis must land exactly on it.
        const string text = "var total = cost * q;";
        var costStart = text.IndexOf("cost", StringComparison.Ordinal);
        var line = new DiffLine(
            DiffLineKind.Added, null, 4, text, [new LineSpan(costStart, 4)]);

        var styled = Compose(line, hasThread: false);
        AssertPartitions(styled);

        var gutterLen = styled.DisplayText.Length - text.Length;
        var emphasized = styled.Runs.Where(r => r.Style.Emphasis).ToList();
        Assert.NotEmpty(emphasized);

        // Every emphasized run lies within the "cost" span (display coords) and keeps a code kind.
        foreach (var r in emphasized)
        {
            Assert.True(r.Start >= gutterLen + costStart);
            Assert.True(r.Start + r.Length <= gutterLen + costStart + 4);
            Assert.False(r.Style.IsGutter);
        }

        // The emphasized runs together spell "cost".
        var spelled = string.Concat(emphasized.Select(r => styled.DisplayText.Substring(r.Start, r.Length)));
        Assert.Equal("cost", spelled);

        // "cost" is an identifier token → emphasis keeps that kind, not Plain.
        Assert.Contains(emphasized, r => r.Style.Token == TokenKind.Identifier);

        // The keyword "var" (outside the span) is present and not emphasized.
        var varRun = styled.Runs.First(r =>
            styled.DisplayText.Substring(r.Start, r.Length) == "var" && r.Style.Token == TokenKind.Keyword);
        Assert.False(varRun.Style.Emphasis);
    }
}
