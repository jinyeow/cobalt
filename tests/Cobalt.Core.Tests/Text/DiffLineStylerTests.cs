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

    [Fact]
    public void WithSearchHits_No_Hits_Leaves_Output_Unchanged()
    {
        var line = new DiffLine(DiffLineKind.Context, 1, 1, "var total = price;");
        var styled = Compose(line, hasThread: false);
        var gutterLen = styled.DisplayText.Length - line.Text.Length;

        var result = DiffLineStyler.WithSearchHits(styled, [], gutterLen);

        Assert.Equal(styled.DisplayText, result.DisplayText);
        Assert.Equal(styled.Runs, result.Runs);
        Assert.All(result.Runs, r => Assert.False(r.Style.SearchHit));
    }

    [Fact]
    public void WithSearchHits_Splits_The_Overlapping_Run_And_Flags_It()
    {
        // "total" is a plain identifier token; the hit lands inside it.
        const string text = "var total = price;";
        var totalStart = text.IndexOf("total", StringComparison.Ordinal);
        var line = new DiffLine(DiffLineKind.Context, 1, 1, text);
        var styled = Compose(line, hasThread: false);
        var gutterLen = styled.DisplayText.Length - text.Length;

        var result = DiffLineStyler.WithSearchHits(styled, [new LineSpan(totalStart, 5)], gutterLen);
        AssertPartitions(result);

        Assert.Equal(styled.DisplayText, result.DisplayText);

        var hits = result.Runs.Where(r => r.Style.SearchHit).ToList();
        Assert.NotEmpty(hits);
        foreach (var r in hits)
        {
            Assert.True(r.Start >= gutterLen + totalStart);
            Assert.True(r.Start + r.Length <= gutterLen + totalStart + 5);
        }
        var spelled = string.Concat(hits.Select(r => result.DisplayText.Substring(r.Start, r.Length)));
        Assert.Equal("total", spelled);

        // Non-hit runs keep SearchHit false and the same styling as before.
        Assert.Contains(result.Runs, r => !r.Style.SearchHit);
        var varRun = result.Runs.First(r =>
            result.DisplayText.Substring(r.Start, r.Length) == "var" && r.Style.Token == TokenKind.Keyword);
        Assert.False(varRun.Style.SearchHit);
    }

    [Fact]
    public void WithSearchHits_Does_Not_Change_Existing_Style_Fields()
    {
        var line = new DiffLine(DiffLineKind.Added, null, 4, "var x = 1;");
        var styled = Compose(line, hasThread: false);
        var gutterLen = styled.DisplayText.Length - line.Text.Length;

        var result = DiffLineStyler.WithSearchHits(styled, [new LineSpan(0, gutterLen)], gutterLen);

        for (var i = 0; i < styled.Runs.Count; i++)
        {
            var before = styled.Runs[i];
            var matching = result.Runs.Where(r => r.Start >= before.Start && r.Start < before.Start + before.Length);
            foreach (var after in matching)
            {
                Assert.Equal(before.Style.Token, after.Style.Token);
                Assert.Equal(before.Style.LineKind, after.Style.LineKind);
                Assert.Equal(before.Style.Emphasis, after.Style.Emphasis);
                Assert.Equal(before.Style.IsGutter, after.Style.IsGutter);
            }
        }
    }
}
