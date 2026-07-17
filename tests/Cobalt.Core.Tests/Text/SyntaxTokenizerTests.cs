using Cobalt.Core.Text.Syntax;

namespace Cobalt.Core.Tests.Text;

public class SyntaxTokenizerTests
{
    private static void AssertPartitions(string line, IReadOnlyList<SyntaxToken> tokens)
    {
        var pos = 0;
        foreach (var t in tokens)
        {
            Assert.Equal(pos, t.Start);
            Assert.True(t.Length > 0, "tokens are non-empty");
            pos += t.Length;
        }
        Assert.Equal(line.Length, pos);
    }

    private static SyntaxToken Find(string line, IReadOnlyList<SyntaxToken> tokens, string text) =>
        tokens.Single(t => line.Substring(t.Start, t.Length) == text);

    [Theory]
    [InlineData("public static void Main() { return 42; }", Language.CSharp)]
    [InlineData("const f = (a) => a + 1; // add", Language.JsTs)]
    [InlineData("def foo(self): return None  # x", Language.Python)]
    [InlineData("{ \"a\": [1, true, null] }", Language.Json)]
    [InlineData("just some plain text 123", Language.None)]
    [InlineData("   ", Language.CSharp)]
    public void Tokens_Partition_The_Line_Exactly(string line, Language lang)
    {
        AssertPartitions(line, SyntaxTokenizer.Tokenize(line, lang));
    }

    [Fact]
    public void Classifying_Identifiers_Allocates_No_Per_Word_Substring()
    {
        // ALGO-3: keyword membership is tested against the line span directly, so an
        // identifier-heavy line must allocate no more than a substring-free control of the
        // identical token structure (a number word per slot). The only difference between
        // the two lines is the per-identifier substring the old path built to probe the set.
        const int words = 200;
        var identifiers = string.Join(" ", Enumerable.Repeat("ab", words));
        var numbers = string.Join(" ", Enumerable.Repeat("12", words));

        SyntaxTokenizer.Tokenize(identifiers, Language.CSharp); // warm up
        SyntaxTokenizer.Tokenize(numbers, Language.CSharp);

        var idAlloc = AllocatedBy(() => SyntaxTokenizer.Tokenize(identifiers, Language.CSharp));
        var numAlloc = AllocatedBy(() => SyntaxTokenizer.Tokenize(numbers, Language.CSharp));

        Assert.True(idAlloc <= numAlloc + 64,
            $"identifier path allocated {idAlloc} vs substring-free control {numAlloc}");
    }

    private static long AllocatedBy(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Repeated_Tokenize_Does_Not_Rebuild_The_Language_Spec()
    {
        // ALGO-4: the (keywords, line-comment, quotes) spec is constant per language, so
        // building a fresh LangSpec + quotes array on every call is pure churn. A short line
        // makes the token list tiny, so a per-call spec allocation would dominate — the bound
        // sits below the per-call cost of rebuilding it.
        const string line = "if";
        const int iterations = 4000;

        for (var i = 0; i < 50; i++)
        {
            SyntaxTokenizer.Tokenize(line, Language.CSharp); // warm up
        }

        var allocated = AllocatedBy(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                SyntaxTokenizer.Tokenize(line, Language.CSharp);
            }
        });

        var perCall = (double)allocated / iterations;
        // The token list for a 2-char line measures ~80 B/call; a rebuilt LangSpec + quotes array
        // added ~62 B (~142 B total). 128 sits clear of both, so a struct-layout tweak to the list
        // cost won't false-positive while a rebuilt spec still trips it.
        Assert.True(perCall < 128, $"per-call allocation {perCall:F1} bytes suggests the spec is rebuilt each call");
    }

    [Fact]
    public void SyntaxToken_Is_A_Value_Type()
    {
        // ALGO-2: a per-token heap object on every code line is pure churn; the token is a
        // tiny immutable (Start, Length, Kind) triple, so it lives inline in the backing
        // array as a struct. Value equality (used across the suite) is preserved.
        Assert.True(typeof(SyntaxToken).IsValueType);
    }

    [Fact]
    public void Empty_Line_Yields_No_Tokens()
    {
        Assert.Empty(SyntaxTokenizer.Tokenize("", Language.CSharp));
        Assert.Empty(SyntaxTokenizer.Tokenize("", Language.None));
    }

    [Fact]
    public void None_Language_Is_A_Single_Plain_Token()
    {
        var line = "whatever goes here";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.None);

        Assert.Single(tokens);
        Assert.Equal(new SyntaxToken(0, line.Length, TokenKind.Plain), tokens[0]);
    }

    [Fact]
    public void CSharp_Keywords_Identifiers_Punctuation()
    {
        const string line = "public static void Main()";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.CSharp);

        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "public").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "static").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "void").Kind);
        Assert.Equal(TokenKind.Identifier, Find(line, tokens, "Main").Kind);
        Assert.Equal(TokenKind.Punctuation, Find(line, tokens, "(").Kind);
        Assert.Equal(TokenKind.Punctuation, Find(line, tokens, ")").Kind);
    }

    [Fact]
    public void CSharp_String_With_Escaped_Quote()
    {
        // Source line: s = "a\"b";
        const string line = "s = \"a\\\"b\";";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.CSharp);

        var str = tokens.Single(t => t.Kind == TokenKind.String);
        Assert.Equal("\"a\\\"b\"", line.Substring(str.Start, str.Length));
    }

    [Fact]
    public void CSharp_Line_Comment_Swallows_To_Eol()
    {
        const string line = "x++; // trailing note";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.CSharp);

        var comment = tokens.Single(t => t.Kind == TokenKind.Comment);
        Assert.Equal("// trailing note", line.Substring(comment.Start, comment.Length));
    }

    [Fact]
    public void CSharp_Numbers_Decimal_Hex_And_Float()
    {
        const string line = "a 42 0x1F 3.14";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.CSharp);

        Assert.Equal(TokenKind.Number, Find(line, tokens, "42").Kind);
        Assert.Equal(TokenKind.Number, Find(line, tokens, "0x1F").Kind);
        Assert.Equal(TokenKind.Number, Find(line, tokens, "3.14").Kind);
    }

    [Fact]
    public void JsTs_Const_Arrow_And_Comment()
    {
        const string line = "const f = () => 1; // c";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.JsTs);

        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "const").Kind);
        Assert.Equal(TokenKind.Operator, Find(line, tokens, "=>").Kind);
        Assert.Equal(TokenKind.Comment, tokens.Single(t => t.Kind == TokenKind.Comment).Kind);
    }

    [Fact]
    public void JsTs_Template_String_To_Closing_Backtick()
    {
        const string line = "const s = `hi ${x}`;";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.JsTs);

        var str = tokens.Single(t => t.Kind == TokenKind.String);
        Assert.Equal("`hi ${x}`", line.Substring(str.Start, str.Length));
    }

    [Fact]
    public void Python_Comment_Keywords_And_String()
    {
        const string line = "def run(self): x = 'hi'  # note";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.Python);

        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "def").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "self").Kind);
        Assert.Equal("'hi'", line.Substring(tokens.Single(t => t.Kind == TokenKind.String).Start,
            tokens.Single(t => t.Kind == TokenKind.String).Length));
        Assert.Equal("# note", line.Substring(tokens.Single(t => t.Kind == TokenKind.Comment).Start,
            tokens.Single(t => t.Kind == TokenKind.Comment).Length));
    }

    [Fact]
    public void Python_None_Is_A_Keyword()
    {
        const string line = "return None";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.Python);

        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "None").Kind);
    }

    [Fact]
    public void Json_Strings_And_Literals_And_Numbers()
    {
        const string line = "\"key\": \"value\", true, false, null, 42";
        var tokens = SyntaxTokenizer.Tokenize(line, Language.Json);

        Assert.Equal(TokenKind.String, Find(line, tokens, "\"key\"").Kind);
        Assert.Equal(TokenKind.String, Find(line, tokens, "\"value\"").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "true").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "false").Kind);
        Assert.Equal(TokenKind.Keyword, Find(line, tokens, "null").Kind);
        Assert.Equal(TokenKind.Number, Find(line, tokens, "42").Kind);
    }
}
