using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

/// <summary>
/// Generative (fuzz) coverage for the partition invariants the diff renderer relies
/// on: the tokenizer must partition a line exactly, changed spans must be well-formed
/// (non-empty, in-bounds), and the styler must produce a gapless, ordered, in-bounds
/// run partition of its display text. A violation here would corrupt the diff pane.
/// </summary>
public class DiffRenderingInvariantsTests
{
    private static readonly char[] Alphabet =
        " \t abcXYZ_09{}()=>+-*/\"'`\\#.,;:|&\nüé😀".Where(c => c != '\n').ToArray();

    private static readonly Language[] Languages =
        [Language.None, Language.CSharp, Language.JsTs, Language.Json, Language.Python];

    private static string RandomLine(Random rng)
    {
        var len = rng.Next(0, 40);
        var chars = new char[len];
        for (var i = 0; i < len; i++)
        {
            chars[i] = Alphabet[rng.Next(Alphabet.Length)];
        }
        return new string(chars);
    }

    [Fact]
    public void Tokenizer_Always_Partitions_The_Line()
    {
        var rng = new Random(1234); // fixed seed → deterministic
        for (var iter = 0; iter < 20_000; iter++)
        {
            var line = RandomLine(rng);
            var language = Languages[rng.Next(Languages.Length)];

            var tokens = SyntaxTokenizer.Tokenize(line, language);

            var pos = 0;
            foreach (var t in tokens)
            {
                Assert.True(t.Length > 0, $"zero-length token in {language} on {Quote(line)}");
                Assert.Equal(pos, t.Start); // gapless + ordered
                pos += t.Length;
            }
            Assert.Equal(line.Length, pos); // total coverage
        }
    }

    [Fact]
    public void IntraLineDiff_Spans_Are_Well_Formed_And_In_Bounds()
    {
        var rng = new Random(5678);
        for (var iter = 0; iter < 20_000; iter++)
        {
            var oldLine = RandomLine(rng);
            var newLine = RandomLine(rng);

            var (oldSpans, newSpans) = IntraLineDiff.Compute(oldLine, newLine);

            AssertSpans(oldSpans, oldLine.Length, oldLine, newLine);
            AssertSpans(newSpans, newLine.Length, oldLine, newLine);
        }
    }

    [Fact]
    public void Styler_Always_Produces_A_Gapless_Run_Partition()
    {
        var rng = new Random(9012);
        for (var iter = 0; iter < 20_000; iter++)
        {
            var oldLine = RandomLine(rng);
            var newLine = RandomLine(rng);
            var diff = DiffService.Unified(oldLine + "\n", newLine + "\n");
            var language = Languages[rng.Next(Languages.Length)];

            foreach (var line in diff.Lines)
            {
                var tokens = SyntaxTokenizer.Tokenize(line.Text, language);
                var styled = DiffLineStyler.Compose(line, tokens, hasThread: rng.Next(2) == 0);

                var pos = 0;
                foreach (var run in styled.Runs)
                {
                    Assert.True(run.Length > 0, $"zero-length run on {Quote(styled.DisplayText)}");
                    Assert.Equal(pos, run.Start); // gapless + ordered
                    pos += run.Length;
                }
                Assert.Equal(styled.DisplayText.Length, pos); // covers the whole display text
            }
        }
    }

    private static void AssertSpans(IReadOnlyList<LineSpan> spans, int length, string oldLine, string newLine)
    {
        var last = 0;
        foreach (var s in spans)
        {
            Assert.True(s.Length > 0, $"zero-length span for {Quote(oldLine)} -> {Quote(newLine)}");
            Assert.True(s.Start >= last, "spans ordered and non-overlapping after merge");
            Assert.True(s.Start + s.Length <= length, "span within bounds");
            last = s.Start + s.Length;
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\t", "\\t") + "\"";
}
