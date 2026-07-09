using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

/// <summary>Pure case-insensitive substring search over a unified <see cref="DiffLine"/> list (ADR 0004).</summary>
public class DiffSearchTests
{
    private static DiffLine Line(string text, DiffLineKind kind = DiffLineKind.Context) => new(kind, 1, 1, text);

    [Fact]
    public void Find_No_Match_Returns_Empty()
    {
        IReadOnlyList<DiffLine> lines = [Line("hello world"), Line("goodbye")];

        var matches = DiffSearch.Find(lines, "xyz");

        Assert.Empty(matches);
    }

    [Fact]
    public void Find_Is_Case_Insensitive()
    {
        IReadOnlyList<DiffLine> lines = [Line("Hello World")];

        var matches = DiffSearch.Find(lines, "hello");

        var match = Assert.Single(matches);
        Assert.Equal(0, match.LineIndex);
        Assert.Equal(new LineSpan(0, 5), match.Span);
    }

    [Fact]
    public void Find_Returns_Multiple_Matches_Per_Line_In_Order()
    {
        IReadOnlyList<DiffLine> lines = [Line("cat cat cat")];

        var matches = DiffSearch.Find(lines, "cat");

        Assert.Equal(3, matches.Count);
        Assert.Equal([0, 4, 8], matches.Select(m => m.Span.Start));
        Assert.All(matches, m => Assert.Equal(0, m.LineIndex));
    }

    [Fact]
    public void Find_Returns_Matches_Across_Lines_Ordered_By_LineIndex()
    {
        IReadOnlyList<DiffLine> lines = [Line("foo"), Line("bar"), Line("foo bar")];

        var matches = DiffSearch.Find(lines, "foo");

        Assert.Equal([0, 2], matches.Select(m => m.LineIndex));
    }

    [Fact]
    public void Next_Wraps_Around_From_The_Last_Match_To_The_First()
    {
        IReadOnlyList<DiffLine> lines = [Line("cat cat")];
        var matches = DiffSearch.Find(lines, "cat");

        Assert.Equal(0, DiffSearch.Next(matches, matches.Count - 1));
    }

    [Fact]
    public void Next_Advances_To_The_Following_Match()
    {
        IReadOnlyList<DiffLine> lines = [Line("cat cat")];
        var matches = DiffSearch.Find(lines, "cat");

        Assert.Equal(1, DiffSearch.Next(matches, 0));
    }

    [Fact]
    public void Prev_Wraps_Around_From_The_First_Match_To_The_Last()
    {
        IReadOnlyList<DiffLine> lines = [Line("cat cat")];
        var matches = DiffSearch.Find(lines, "cat");

        Assert.Equal(matches.Count - 1, DiffSearch.Prev(matches, 0));
    }

    [Fact]
    public void Prev_Retreats_To_The_Preceding_Match()
    {
        IReadOnlyList<DiffLine> lines = [Line("cat cat")];
        var matches = DiffSearch.Find(lines, "cat");

        Assert.Equal(0, DiffSearch.Prev(matches, 1));
    }

    [Fact]
    public void Next_On_No_Matches_Returns_CurrentIndex_Unchanged()
    {
        var matches = DiffSearch.Find([Line("nothing")], "zzz");

        Assert.Equal(-1, DiffSearch.Next(matches, -1));
    }

    [Fact]
    public void Prev_On_No_Matches_Returns_CurrentIndex_Unchanged()
    {
        var matches = DiffSearch.Find([Line("nothing")], "zzz");

        Assert.Equal(-1, DiffSearch.Prev(matches, -1));
    }
}
