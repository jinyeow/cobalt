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

public sealed record SyntaxToken(int Start, int Length, TokenKind Kind);

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
        IReadOnlySet<string> Keywords,
        string? LineComment,
        char[] Quotes);

    private static readonly IReadOnlySet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "const", "var", "void",
        "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break",
        "continue", "new", "class", "struct", "record", "interface", "enum", "namespace", "using",
        "this", "base", "null", "true", "false", "int", "long", "short", "byte", "uint", "ulong",
        "string", "bool", "double", "float", "decimal", "char", "object", "async", "await", "get",
        "set", "init", "sealed", "override", "virtual", "abstract", "throw", "try", "catch", "finally",
        "in", "out", "ref", "is", "as", "typeof", "nameof", "yield", "params", "where", "when",
    };

    private static readonly IReadOnlySet<string> JsTsKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "const", "let", "var", "function", "return", "if", "else", "for", "while", "do", "switch",
        "case", "default", "break", "continue", "new", "class", "extends", "super", "this", "null",
        "undefined", "true", "false", "import", "export", "from", "async", "await", "yield", "typeof",
        "instanceof", "in", "of", "void", "delete", "try", "catch", "finally", "throw", "interface",
        "type", "enum", "public", "private", "protected", "readonly", "static", "as", "namespace",
    };

    private static readonly IReadOnlySet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "def", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "is", "None",
        "True", "False", "class", "import", "from", "as", "pass", "break", "continue", "with", "try",
        "except", "finally", "raise", "lambda", "yield", "global", "nonlocal", "self", "async", "await",
        "del", "assert",
    };

    private static readonly IReadOnlySet<string> JsonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "true", "false", "null",
    };

    private static readonly IReadOnlySet<string> NoKeywords = new HashSet<string>(StringComparer.Ordinal);

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
        var tokens = new List<SyntaxToken>();
        var i = 0;
        var n = line.Length;

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
                var word = line[start..i];
                var kind = spec.Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
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

    private static LangSpec SpecFor(Language language) => language switch
    {
        Language.CSharp => new LangSpec(CSharpKeywords, "//", ['"', '\'']),
        Language.JsTs => new LangSpec(JsTsKeywords, "//", ['"', '\'', '`']),
        Language.Python => new LangSpec(PythonKeywords, "#", ['\'', '"']),
        Language.Json => new LangSpec(JsonKeywords, null, ['"']),
        _ => new LangSpec(NoKeywords, null, []),
    };

    private static bool MatchesAt(string line, int i, string token) =>
        i + token.Length <= line.Length && string.CompareOrdinal(line, i, token, 0, token.Length) == 0;

    private static bool IsPunctuation(char c) => c is '(' or ')' or '[' or ']' or '{' or '}' or ';' or ',' or ':';

    private static bool IsOperatorChar(char c) =>
        c is '+' or '-' or '*' or '/' or '=' or '<' or '>' or '!' or '&' or '|' or '%' or '^' or '~'
            or '?' or '.' or '@' or '$' or '\\';
}
