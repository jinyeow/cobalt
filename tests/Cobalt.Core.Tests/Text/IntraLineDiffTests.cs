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

    [Fact]
    public void Lines_Over_The_Length_Guard_Return_Empty_Even_For_A_Small_Word_Change()
    {
        // A single-word change would normally clear the similarity guard (low ChangedRatio)
        // and produce spans; the length guard must short-circuit before that regardless.
        var oldLine = string.Join(" ", Enumerable.Repeat("word", 480)) + " end";
        var newLine = string.Join(" ", Enumerable.Repeat("word", 480)) + " tail";
        Assert.True(oldLine.Length > 2000, "fixture must exceed the length guard");

        var (oldSpans, newSpans) = IntraLineDiff.Compute(oldLine, newLine);

        Assert.Empty(oldSpans);
        Assert.Empty(newSpans);
    }

    // ---- ALGO-1: length-ratio early-out before the word-level Myers diff ----

    [Fact]
    public void Mismatched_Length_Pair_Skips_The_Myers_Diff_And_Allocates_Nothing()
    {
        // The shorter side is under (1 - MaxChangedRatio) of the longer, so the similarity
        // guard is guaranteed to drop every span — the early-out must return before paying
        // the Myers diff's allocations. Both lines share a "short" prefix so this is not the
        // trivial no-common-content case; only the length ratio decides it.
        const string oldLine = "short";
        const string newLine = "short and now this is a very much longer line that continues on and on";
        Assert.True((double)oldLine.Length / newLine.Length < 0.4, "fixture must trip the ratio early-out");

        IntraLineDiff.Compute(oldLine, newLine); // warm up JIT + any static init

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (oldSpans, newSpans) = IntraLineDiff.Compute(oldLine, newLine);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(oldSpans);
        Assert.Empty(newSpans);
        Assert.True(allocated == 0, $"early-out path must not allocate, but allocated {allocated} bytes");
    }

    [Fact]
    public void Early_Out_Is_Bit_For_Bit_Identical_To_Computing_Without_It()
    {
        // Randomized parity: for every pair the early-out must return exactly what the
        // full compute (no early-out) returns — the reference is the same algorithm with
        // the optimisation removed, so a divergence is a real behaviour change.
        var rng = new Random(20260716);
        const string alphabet = "abcde ";
        for (var iter = 0; iter < 2000; iter++)
        {
            var oldLine = RandomLine(rng, alphabet);
            var newLine = RandomLine(rng, alphabet);

            var actual = IntraLineDiff.Compute(oldLine, newLine);
            var reference = IntraLineDiff.ComputeWithoutEarlyOut(oldLine, newLine);

            Assert.Equal(reference.OldSpans, actual.OldSpans);
            Assert.Equal(reference.NewSpans, actual.NewSpans);
        }
    }

    private static string RandomLine(Random rng, string alphabet)
    {
        var length = rng.Next(0, 40);
        return string.Create(length, (rng, alphabet), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = state.alphabet[state.rng.Next(state.alphabet.Length)];
            }
        });
    }
}
