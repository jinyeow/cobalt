using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

public class IntraLineDiffTests
{
    private static string Slice(string line, LineSpan s) => line.Substring(s.Start, s.Length);

    private static void AssertWellFormed(string line, IReadOnlyList<LineSpan> spans)
    {
        var last = 0;
        foreach (var s in spans)
        {
            Assert.True(s.Start >= 0, "start within bounds");
            Assert.True(s.Length > 0, "non-empty span");
            Assert.True(s.Start + s.Length <= line.Length, "end within bounds");
            Assert.True(s.Start >= last, "ordered / non-overlapping");
            last = s.Start + s.Length;
        }
    }

    [Fact]
    public void Single_Word_Replaced_Spans_Cover_Exactly_That_Word()
    {
        var (oldSpans, newSpans) = IntraLineDiff.Compute("var total = price * q;", "var total = cost * q;");

        Assert.Single(oldSpans);
        Assert.Single(newSpans);
        Assert.Equal("price", Slice("var total = price * q;", oldSpans[0]));
        Assert.Equal("cost", Slice("var total = cost * q;", newSpans[0]));
    }

    [Fact]
    public void Insertion_Only_Has_Empty_Old_Side()
    {
        var (oldSpans, newSpans) = IntraLineDiff.Compute("hello world", "hello there world");

        Assert.Empty(oldSpans);
        Assert.Single(newSpans);
        Assert.Contains("there", Slice("hello there world", newSpans[0]));
    }

    [Fact]
    public void Adjacent_Changed_Words_Merge_Into_One_Span()
    {
        var (_, newSpans) = IntraLineDiff.Compute("total = price;", "total = cost now;");

        Assert.Single(newSpans);
        Assert.Equal("cost now", Slice("total = cost now;", newSpans[0]));
    }

    [Fact]
    public void Identical_Lines_Have_No_Spans()
    {
        var (oldSpans, newSpans) = IntraLineDiff.Compute("same line", "same line");

        Assert.Empty(oldSpans);
        Assert.Empty(newSpans);
    }

    [Fact]
    public void Total_Rewrite_Over_Similarity_Threshold_Returns_Empty()
    {
        var (oldSpans, newSpans) = IntraLineDiff.Compute("abcdef", "xyzuvw");

        Assert.Empty(oldSpans);
        Assert.Empty(newSpans);
    }

    [Fact]
    public void Empty_Old_Line_Against_Text_Returns_Empty()
    {
        var (oldSpans, newSpans) = IntraLineDiff.Compute("", "brand new text");

        Assert.Empty(oldSpans);
        Assert.Empty(newSpans);
    }

    [Fact]
    public void Spans_Are_In_Bounds_Ordered_And_Non_Overlapping()
    {
        const string oldLine = "keep aa bb keep tail";
        const string newLine = "keep xx yy keep tail";
        var (oldSpans, newSpans) = IntraLineDiff.Compute(oldLine, newLine);

        AssertWellFormed(oldLine, oldSpans);
        AssertWellFormed(newLine, newSpans);
        Assert.NotEmpty(oldSpans);
        Assert.NotEmpty(newSpans);
    }
}
