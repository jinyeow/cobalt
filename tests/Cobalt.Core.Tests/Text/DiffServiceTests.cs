using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

public class DiffServiceTests
{
    [Fact]
    public void Unchanged_Text_Has_Only_Context_Lines()
    {
        var diff = DiffService.Unified("a\nb\nc\n", "a\nb\nc\n");

        Assert.All(diff.Lines, l => Assert.Equal(DiffLineKind.Context, l.Kind));
    }

    [Fact]
    public void Inserted_Line_Is_Marked_Added_With_New_Number()
    {
        var diff = DiffService.Unified("a\nc\n", "a\nb\nc\n");

        var added = diff.Lines.Single(l => l.Kind == DiffLineKind.Added);
        Assert.Equal("b", added.Text);
        Assert.Equal(2, added.NewLineNumber);
        Assert.Null(added.OldLineNumber);
    }

    [Fact]
    public void Deleted_Line_Is_Marked_Removed_With_Old_Number()
    {
        var diff = DiffService.Unified("a\nb\nc\n", "a\nc\n");

        var removed = diff.Lines.Single(l => l.Kind == DiffLineKind.Removed);
        Assert.Equal("b", removed.Text);
        Assert.Equal(2, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);
    }

    [Fact]
    public void Context_Lines_Carry_Both_Line_Numbers()
    {
        var diff = DiffService.Unified("a\nb\n", "a\nX\n");

        var context = diff.Lines.First(l => l.Kind == DiffLineKind.Context);
        Assert.Equal("a", context.Text);
        Assert.Equal(1, context.OldLineNumber);
        Assert.Equal(1, context.NewLineNumber);
    }

    [Fact]
    public void Counts_Additions_And_Deletions()
    {
        var diff = DiffService.Unified("a\nb\nc\n", "a\nx\ny\nc\n");

        Assert.Equal(2, diff.Additions);
        Assert.Equal(1, diff.Deletions);
    }

    [Fact]
    public void Added_File_All_Lines_Added()
    {
        var diff = DiffService.Unified("", "new1\nnew2\n");

        Assert.Equal(2, diff.Additions);
        Assert.Equal(0, diff.Deletions);
    }

    [Fact]
    public void Binary_Content_Is_Flagged_Not_Diffed()
    {
        var diff = DiffService.Unified("text\n", "abc\0def\n");

        Assert.True(diff.IsBinary);
        Assert.Empty(diff.Lines);
    }

    [Fact]
    public void Modified_Line_Fills_ChangedSpans_On_Both_Sides()
    {
        var diff = DiffService.Unified("var total = price * q;\n", "var total = cost * q;\n");

        var removed = diff.Lines.Single(l => l.Kind == DiffLineKind.Removed);
        var added = diff.Lines.Single(l => l.Kind == DiffLineKind.Added);

        Assert.NotNull(removed.ChangedSpans);
        Assert.Single(removed.ChangedSpans!);
        var rs = removed.ChangedSpans![0];
        Assert.Equal("price", removed.Text.Substring(rs.Start, rs.Length));

        Assert.NotNull(added.ChangedSpans);
        Assert.Single(added.ChangedSpans!);
        var as0 = added.ChangedSpans![0];
        Assert.Equal("cost", added.Text.Substring(as0.Start, as0.Length));
    }

    [Fact]
    public void Pure_Insertion_Has_No_ChangedSpans()
    {
        var diff = DiffService.Unified("a\nc\n", "a\nb\nc\n");

        var added = diff.Lines.Single(l => l.Kind == DiffLineKind.Added);
        Assert.True(added.ChangedSpans is null || added.ChangedSpans.Count == 0);
    }

    [Fact]
    public void Two_Removed_One_Added_Pairs_Only_The_First_Removed()
    {
        var diff = DiffService.Unified("price one\nprice two\n", "cost one\n");

        var removed = diff.Lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();
        Assert.Equal(2, removed.Count);

        // First removed pairs with the single added line and gets a word span.
        var first = removed[0].ChangedSpans;
        Assert.NotNull(first);
        Assert.NotEmpty(first);

        // Second removed line is unpaired: no intra-line info.
        var second = removed[1].ChangedSpans;
        Assert.True(second is null || second.Count == 0);
    }

    [Fact]
    public void Large_File_Over_Limit_Is_Skipped()
    {
        var big = string.Concat(Enumerable.Repeat("line\n", 60_000));

        var diff = DiffService.Unified("", big, maxLines: 50_000);

        Assert.True(diff.TooLarge);
        Assert.Empty(diff.Lines);
    }
}
