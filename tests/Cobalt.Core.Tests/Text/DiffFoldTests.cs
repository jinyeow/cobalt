using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

/// <summary>
/// Pure context-folding over a unified <see cref="DiffLine"/> list (ADR 0004): maximal
/// runs of <see cref="DiffLineKind.Context"/> lines more than <c>radius</c> away from
/// any change collapse into a single fold marker.
/// </summary>
public class DiffFoldTests
{
    private static DiffLine Ctx(int n = 1) => new(DiffLineKind.Context, n, n, $"ctx{n}");
    private static DiffLine Add(int n = 1) => new(DiffLineKind.Added, null, n, $"add{n}");

    private static IReadOnlyList<DiffLine> ContextRun(int count) =>
        Enumerable.Range(1, count).Select(Ctx).ToList();

    [Fact]
    public void No_Changes_Folds_The_Whole_File_Into_One_Fold()
    {
        var lines = ContextRun(10);
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        var row = Assert.Single(rows);
        Assert.Equal(10, row.HiddenCount);
        Assert.NotNull(row.FoldId);
        Assert.Null(row.LineIndex);
    }

    [Fact]
    public void No_Changes_And_Short_File_Still_Folds_As_One()
    {
        // Even a run shorter than the radius folds entirely when there's no hunk on either side.
        var lines = ContextRun(2);
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        var row = Assert.Single(rows);
        Assert.Equal(2, row.HiddenCount);
    }

    [Fact]
    public void Change_At_Start_Keeps_Radius_Lines_After_It_And_Folds_The_Rest()
    {
        // Add, then 10 context lines: only the trailing tail beyond radius folds.
        List<DiffLine> lines = [Add(1), .. Enumerable.Range(1, 10).Select(Ctx)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        // change line + 3 visible context + 1 fold marker (hiding the remaining 7).
        Assert.Equal(5, rows.Count);
        Assert.Equal(0, rows[0].LineIndex);
        Assert.Equal(1, rows[1].LineIndex);
        Assert.Equal(2, rows[2].LineIndex);
        Assert.Equal(3, rows[3].LineIndex);
        Assert.Equal(7, rows[4].HiddenCount);
    }

    [Fact]
    public void Change_At_End_Keeps_Radius_Lines_Before_It_And_Folds_The_Rest()
    {
        // 10 context lines, then Add: only the leading head beyond radius folds.
        List<DiffLine> lines = [.. Enumerable.Range(1, 10).Select(Ctx), Add(99)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        // fold marker (hiding first 7) + 3 visible context + change line.
        Assert.Equal(5, rows.Count);
        Assert.Equal(7, rows[0].HiddenCount);
        Assert.Equal(7, rows[1].LineIndex);
        Assert.Equal(8, rows[2].LineIndex);
        Assert.Equal(9, rows[3].LineIndex);
        Assert.Equal(10, rows[4].LineIndex);
    }

    [Fact]
    public void Adjacent_Changes_With_A_Short_Gap_Have_No_Fold_Between_Them()
    {
        // Add, 4 context lines (<= 2*radius=6), Add: the gap stays fully visible.
        List<DiffLine> lines = [Add(1), Ctx(1), Ctx(2), Ctx(3), Ctx(4), Add(2)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Null(r.FoldId));
    }

    [Fact]
    public void Gap_Exactly_At_The_Radius_Boundary_Has_No_Fold()
    {
        // Two changes with exactly 2*radius=6 context lines between: leftKeep+rightKeep == runLength.
        List<DiffLine> lines = [Add(1), .. Enumerable.Range(1, 6).Select(Ctx), Add(2)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Null(r.FoldId));
    }

    [Fact]
    public void Gap_One_Line_Past_The_Radius_Boundary_Folds_A_Single_Line()
    {
        // 7 context lines between changes: one line over budget folds into a 1-line fold.
        List<DiffLine> lines = [Add(1), .. Enumerable.Range(1, 7).Select(Ctx), Add(2)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var rows = state.Rows();

        // change + 3 ctx + 1 fold(hiding 1) + 3 ctx + change = 9 rows
        Assert.Equal(9, rows.Count);
        var fold = Assert.Single(rows, r => r.FoldId is not null);
        Assert.Equal(1, fold.HiddenCount);
    }

    [Fact]
    public void Expand_One_Fold_Replaces_It_With_The_Hidden_Lines()
    {
        var lines = ContextRun(10);
        var state = DiffFoldState.Create(lines, radius: 3);
        var foldId = state.Rows().Single().FoldId!.Value;

        var expanded = state.Expand(foldId);
        var rows = expanded.Rows();

        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.Null(r.FoldId));
        Assert.Equal(Enumerable.Range(0, 10).Select(n => (int?)n), rows.Select(r => r.LineIndex));
    }

    [Fact]
    public void Expand_Does_Not_Mutate_The_Original_State()
    {
        var lines = ContextRun(10);
        var state = DiffFoldState.Create(lines, radius: 3);
        var foldId = state.Rows().Single().FoldId!.Value;

        state.Expand(foldId);

        Assert.Single(state.Rows());
    }

    [Fact]
    public void ExpandAll_Replaces_Every_Fold_With_Its_Hidden_Lines()
    {
        List<DiffLine> lines = [Add(1), .. Enumerable.Range(1, 7).Select(Ctx), Add(2)];
        var state = DiffFoldState.Create(lines, radius: 3);

        var expanded = state.ExpandAll();
        var rows = expanded.Rows();

        Assert.Equal(lines.Count, rows.Count);
        Assert.All(rows, r => Assert.Null(r.FoldId));
    }

    [Fact]
    public void Expanded_Line_Rows_Carry_The_Original_DiffLine()
    {
        var lines = ContextRun(5);
        var state = DiffFoldState.Create(lines, radius: 3);
        var foldId = state.Rows().Single().FoldId!.Value;

        var rows = state.Expand(foldId).Rows();

        Assert.Equal(lines[2], rows[2].Line);
    }
}
