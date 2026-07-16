using System.Collections.Frozen;

namespace Cobalt.Core.Text.Syntax;

public enum TokenKind
{
    Plain,
    Keyword,
    Identifier,
    Number,
    String,
    Comment,
    Operator,
    Punctuation,
}

public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// A single-pass, strictly line-local syntax tokenizer for C#, JS/TS, JSON and
/// Python (SPEC §3 review — syntax color). No state carries across lines, so
/// block comments / multi-line strings tokenize as code inside their interior
/// (deliberate cut, ADR 0010). Output tokens partition <c>[0, line.Length)</c>
/// exactly and in order. Pure.
/// </summary>
public static class SyntaxTokenizer
{
    private sealed record LangSpec(
        FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords,
        string? LineComment,
        char[] Quotes);

    private static readonly FrozenSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "const", "var", "void",
        "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break",
        "continue", "new", "class", "struct", "record", "interface", "enum", "namespace", "using",
        "this", "base", "null", "true", "false", "int", "long", "short", "byte", "uint", "ulong",
        "string", "bool", "double", "float", "decimal", "char", "object", "async", "await", "get",
        "set", "init", "sealed", "override", "virtual", "abstract", "throw", "try", "catch", "finally",
        "in", "out", "ref", "is", "as", "typeof", "nameof", "yield", "params", "where", "when",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> JsTsKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "const", "let", "var", "function", "return", "if", "else", "for", "while", "do", "switch",
        "case", "default", "break", "continue", "new", "class", "extends", "super", "this", "null",
        "undefined", "true", "false", "import", "export", "from", "async", "await", "yield", "typeof",
        "instanceof", "in", "of", "void", "delete", "try", "catch", "finally", "throw", "interface",
        "type", "enum", "public", "private", "protected", "readonly", "static", "as", "namespace",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "def", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "is", "None",
        "True", "False", "class", "import", "from", "as", "pass", "break", "continue", "with", "try",
        "except", "finally", "raise", "lambda", "yield", "global", "nonlocal", "self", "async", "await",
        "del", "assert",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> JsonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "true", "false", "null",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> NoKeywords =
        new HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<SyntaxToken> Tokenize(string line, Language language)
    {
        if (line.Length == 0)
        {
            return [];
        }
        if (language == Language.None)
        {
            return [new SyntaxToken(0, line.Length, TokenKind.Plain)];
        }

        var spec = SpecFor(language);
        var n = line.Length;
        // Pre-size to the token-count upper bound (one token per char) so a dense line never
        // pays a resize; typical code lines tokenize well under this.
        var tokens = new List<SyntaxToken>(n);
        var i = 0;

        while (i < n)
        {
            var c = line[i];

            // Whitespace run.
            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < n && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Plain));
                continue;
            }

            // Line comment to end of line.
            if (spec.LineComment is { } lc && MatchesAt(line, i, lc))
            {
                tokens.Add(new SyntaxToken(i, n - i, TokenKind.Comment));
                i = n;
                continue;
            }

            // String / char / template literal.
            if (Array.IndexOf(spec.Quotes, c) >= 0)
            {
                var start = i;
                var quote = c;
                i++;
                while (i < n)
                {
                    if (line[i] == '\\' && i + 1 < n)
                    {
                        i += 2;
                        continue;
                    }
                    if (line[i] == quote)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number literal (incl. 0x.., decimals, digit-group underscores).
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword.
            if (c == '_' || char.IsLetter(c))
            {
                var start = i;
                while (i < n && (line[i] == '_' || char.IsLetterOrDigit(line[i])))
                {
                    i++;
                }
                var kind = spec.Keywords.Contains(line.AsSpan(start, i - start))
                    ? TokenKind.Keyword
                    : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            // Punctuation (single char).
            if (IsPunctuation(c))
            {
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Punctuation));
                i++;
                continue;
            }

            // Operator: a run of operator characters (so "=>" / "==" are one token).
            if (IsOperatorChar(c))
            {
                var start = i;
                while (i < n && IsOperatorChar(line[i]))
                {
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Operator));
                continue;
            }

            // Anything else: one Plain char (keeps the partition total).
            tokens.Add(new SyntaxToken(i, 1, TokenKind.Plain));
            i++;
        }

        return tokens;
    }

    // One immutable spec per language, built once at static init — the (keywords, comment,
    // quotes) triple never varies per call, so there is nothing to rebuild on each Tokenize.
    private static readonly LangSpec CSharpSpec = new(Lookup(CSharpKeywords), "//", ['"', '\'']);
    private static readonly LangSpec JsTsSpec = new(Lookup(JsTsKeywords), "//", ['"', '\'', '`']);
    private static readonly LangSpec PythonSpec = new(Lookup(PythonKeywords), "#", ['\'', '"']);
    private static readonly LangSpec JsonSpec = new(Lookup(JsonKeywords), null, ['"']);
    private static readonly LangSpec NoneSpec = new(Lookup(NoKeywords), null, []);

    private static LangSpec SpecFor(Language language) => language switch
    {
        Language.CSharp => CSharpSpec,
        Language.JsTs => JsTsSpec,
        Language.Python => PythonSpec,
        Language.Json => JsonSpec,
        _ => NoneSpec,
    };

    private static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup(FrozenSet<string> set) =>
        set.GetAlternateLookup<ReadOnlySpan<char>>();

    private static bool MatchesAt(string line, int i, string token) =>
        i + token.Length <= line.Length && string.CompareOrdinal(line, i, token, 0, token.Length) == 0;

    private static bool IsPunctuation(char c) => c is '(' or ')' or '[' or ']' or '{' or '}' or ';' or ',' or ':';

    private static bool IsOperatorChar(char c) =>
        c is '+' or '-' or '*' or '/' or '=' or '<' or '>' or '!' or '&' or '|' or '%' or '^' or '~'
            or '?' or '.' or '@' or '$' or '\\';
}
