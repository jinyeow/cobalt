using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

/// <summary>
/// Pure hunk/thread navigation over a unified <see cref="DiffLine"/> list (ADR 0004).
/// A "hunk" is a maximal run of non-<see cref="DiffLineKind.Context"/> lines.
/// </summary>
public class DiffNavigatorTests
{
    private static DiffLine Ctx() => new(DiffLineKind.Context, 1, 1, "ctx");
    private static DiffLine Add() => new(DiffLineKind.Added, null, 1, "add");
    private static DiffLine Rem() => new(DiffLineKind.Removed, 1, null, "rem");

    // Index: 0    1    2    3    4    5    6    7    8
    //        ctx  add  add  ctx  ctx  rem  ctx  add  ctx
    //             \--hunk1--/         \h2/       \h3/
    private static readonly IReadOnlyList<DiffLine> TwoHunks =
        [Ctx(), Add(), Add(), Ctx(), Ctx(), Rem(), Ctx(), Add(), Ctx()];

    [Fact]
    public void NextHunk_On_Empty_Lines_Returns_FromIndex()
    {
        IReadOnlyList<DiffLine> lines = [];
        Assert.Equal(0, DiffNavigator.NextHunk(lines, 0));
    }

    [Fact]
    public void NextHunk_Single_Hunk_Jumps_To_Its_First_Line()
    {
        IReadOnlyList<DiffLine> lines = [Ctx(), Add(), Add(), Ctx()];
        Assert.Equal(1, DiffNavigator.NextHunk(lines, 0));
    }

    [Fact]
    public void NextHunk_From_Before_First_Hunk_Lands_On_First_Hunk_Start()
    {
        Assert.Equal(1, DiffNavigator.NextHunk(TwoHunks, 0));
    }

    [Fact]
    public void NextHunk_Adjacent_Hunks_Skips_To_The_Following_Hunk_Start()
    {
        // From inside hunk1 (index 1), next hunk is hunk2 at index 5.
        Assert.Equal(5, DiffNavigator.NextHunk(TwoHunks, 1));
    }

    [Fact]
    public void NextHunk_From_Exactly_A_Hunk_Start_Moves_Off_It()
    {
        Assert.Equal(7, DiffNavigator.NextHunk(TwoHunks, 5));
    }

    [Fact]
    public void NextHunk_Count_Aware_Skips_Multiple_Hunks()
    {
        // 1st hunk after 0 is at 1; the 2nd is at 5.
        Assert.Equal(5, DiffNavigator.NextHunk(TwoHunks, 0, count: 2));
    }

    [Fact]
    public void NextHunk_Clamps_At_The_Last_Hunk()
    {
        Assert.Equal(7, DiffNavigator.NextHunk(TwoHunks, 0, count: 10));
    }

    [Fact]
    public void NextHunk_No_Match_Returns_FromIndex()
    {
        Assert.Equal(7, DiffNavigator.NextHunk(TwoHunks, 7));
    }

    [Fact]
    public void PrevHunk_Single_Hunk_Jumps_To_Its_First_Line()
    {
        IReadOnlyList<DiffLine> lines = [Ctx(), Add(), Add(), Ctx()];
        Assert.Equal(1, DiffNavigator.PrevHunk(lines, 3));
    }

    [Fact]
    public void PrevHunk_From_Inside_Hunk2_Lands_On_Hunk1_Start()
    {
        Assert.Equal(1, DiffNavigator.PrevHunk(TwoHunks, 5));
    }

    [Fact]
    public void PrevHunk_From_Exactly_A_Hunk_Start_Moves_Off_It()
    {
        // Index 7 is exactly hunk3's start; prev must land on hunk2 (index 5), not stay at 7.
        Assert.Equal(5, DiffNavigator.PrevHunk(TwoHunks, 7));
    }

    [Fact]
    public void PrevHunk_Count_Aware_Skips_Multiple_Hunks()
    {
        // 1st hunk before 8 is at 7; the 2nd is at 5.
        Assert.Equal(5, DiffNavigator.PrevHunk(TwoHunks, 8, count: 2));
    }

    [Fact]
    public void PrevHunk_Clamps_At_The_First_Hunk()
    {
        Assert.Equal(1, DiffNavigator.PrevHunk(TwoHunks, 8, count: 10));
    }

    [Fact]
    public void PrevHunk_No_Match_Returns_FromIndex()
    {
        Assert.Equal(1, DiffNavigator.PrevHunk(TwoHunks, 1));
    }

    [Fact]
    public void NextThread_Empty_Lines_Returns_FromIndex()
    {
        IReadOnlyList<DiffLine> lines = [];
        Assert.Equal(0, DiffNavigator.NextThread(lines, 0, _ => false));
    }

    [Fact]
    public void NextThread_Jumps_To_Next_Anchored_Index()
    {
        var threads = new HashSet<int> { 2, 5 };
        Assert.Equal(2, DiffNavigator.NextThread(TwoHunks, 0, threads.Contains));
        Assert.Equal(5, DiffNavigator.NextThread(TwoHunks, 2, threads.Contains));
    }

    [Fact]
    public void NextThread_Count_Aware()
    {
        var threads = new HashSet<int> { 2, 5, 7 };
        Assert.Equal(7, DiffNavigator.NextThread(TwoHunks, 0, threads.Contains, count: 3));
    }

    [Fact]
    public void NextThread_No_Match_Returns_FromIndex()
    {
        Assert.Equal(3, DiffNavigator.NextThread(TwoHunks, 3, _ => false));
    }

    [Fact]
    public void PrevThread_Jumps_To_Previous_Anchored_Index()
    {
        var threads = new HashSet<int> { 2, 5 };
        Assert.Equal(2, DiffNavigator.PrevThread(TwoHunks, 5, threads.Contains));
        Assert.Equal(5, DiffNavigator.PrevThread(TwoHunks, 8, threads.Contains));
    }

    [Fact]
    public void PrevThread_No_Match_Returns_FromIndex()
    {
        Assert.Equal(3, DiffNavigator.PrevThread(TwoHunks, 3, _ => false));
    }
}
