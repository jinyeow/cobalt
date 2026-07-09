using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

/// <summary>
/// The pure side-by-side projection (Item 3, ADR 0010 philosophy): unified diff
/// lines → paired left/right rows for a two-column view, and a row→(leftIndex,
/// rightIndex) map so comment anchoring resolves to the right original line/side.
/// Pairing must match DiffService's k-th-removed↔k-th-added rule so the split rows
/// align with the intra-line emphasis DiffService already computed.
/// </summary>
public class SideBySideComposerTests
{
    private static DiffLine Ctx(int oldNo, int newNo, string text) =>
        new(DiffLineKind.Context, oldNo, newNo, text);

    private static DiffLine Rem(int oldNo, string text) => new(DiffLineKind.Removed, oldNo, null, text);

    private static DiffLine Add(int newNo, string text) => new(DiffLineKind.Added, null, newNo, text);

    [Fact]
    public void Pair_Aligns_Kth_Removed_With_Kth_Added_Then_Leaves_Extras_One_Sided()
    {
        DiffLine[] lines =
        [
            Ctx(1, 1, "a"),    // 0
            Rem(2, "old1"),    // 1
            Rem(3, "old2"),    // 2
            Rem(4, "old3"),    // 3
            Add(2, "new1"),    // 4
            Ctx(5, 3, "b"),    // 5
        ];

        var rows = SideBySideComposer.Pair(lines);

        Assert.Equal(
            [(0, 0), (1, 4), (2, null), (3, null), (5, 5)],
            rows.Select(r => (r.LeftIndex, r.RightIndex)));
    }

    [Fact]
    public void Pair_Added_Run_With_No_Removed_Is_All_Right_Side()
    {
        DiffLine[] lines = [Ctx(1, 1, "a"), Add(2, "new1"), Add(3, "new2")];

        var rows = SideBySideComposer.Pair(lines);

        Assert.Equal(
            [(0, 0), (null, 1), (null, 2)],
            rows.Select(r => (r.LeftIndex, r.RightIndex)));
    }

    // ---- Compose (two-column layout) ----

    private const int Width = 20;
    private static readonly int LeftEnd = Width;
    private static readonly int RightStart = Width + SideBySideComposer.Separator.Length;

    private static void AssertGaplessPartition(StyledLine styled)
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

    private static StyledLine Compose1(DiffLine[] lines, Func<DiffLine, bool>? hasThread = null)
    {
        var rows = SideBySideComposer.Pair(lines);
        var styled = SideBySideComposer.Compose(lines, rows, Language.None, hasThread ?? (_ => false), Width);
        return Assert.Single(styled);
    }

    [Fact]
    public void Compose_Lays_Out_Two_Aligned_Columns_Of_Equal_Width()
    {
        var row = Compose1([Ctx(1, 1, "x")]);

        Assert.Equal((Width * 2) + SideBySideComposer.Separator.Length, row.DisplayText.Length);
        Assert.Contains("x", row.DisplayText[..LeftEnd]);   // old side
        Assert.Contains("x", row.DisplayText[RightStart..]); // new side
        AssertGaplessPartition(row);
    }

    [Fact]
    public void Compose_Modified_Pair_Puts_Old_Left_And_New_Right_With_Diff_Kinds()
    {
        var row = Compose1([Rem(2, "old"), Add(2, "new")]); // Pair → (0, 1)

        Assert.Contains("old", row.DisplayText[..LeftEnd]);
        Assert.Contains("new", row.DisplayText[RightStart..]);
        Assert.Contains(row.Runs, r => r.Style.LineKind == DiffLineKind.Removed); // left tint
        Assert.Contains(row.Runs, r => r.Style.LineKind == DiffLineKind.Added);   // right tint
        AssertGaplessPartition(row);
    }

    [Fact]
    public void Compose_One_Sided_Row_Leaves_The_Opposite_Column_Blank()
    {
        var row = Compose1([Rem(2, "gone")]); // Pair → (0, null)

        Assert.Contains("gone", row.DisplayText[..LeftEnd]);
        Assert.True(string.IsNullOrWhiteSpace(row.DisplayText[RightStart..]));
        AssertGaplessPartition(row);
    }

    [Fact]
    public void Compose_Clips_A_Long_Line_To_The_Column_Width()
    {
        var row = Compose1([Ctx(1, 1, new string('a', 200))]);

        Assert.Equal(Width, row.DisplayText[..LeftEnd].Length);
        Assert.Equal((Width * 2) + SideBySideComposer.Separator.Length, row.DisplayText.Length);
        AssertGaplessPartition(row);
    }

    [Fact]
    public void Compose_Puts_The_Thread_Marker_On_The_Anchored_Side()
    {
        var row = Compose1([Ctx(1, 1, "x")], hasThread: _ => true);

        Assert.Contains('●', row.DisplayText);
    }
}
