using System.Text.RegularExpressions;
using Markdig;

namespace Cobalt.Core.Text;

public readonly record struct MarkdownAnalysis(string Markdown, bool Lossy);

/// <summary>
/// Converts between the HTML that Azure DevOps stores and the Markdown the user
/// edits (SPEC §2). Round-trip is lossy on rich HTML (styled tables, inline CSS),
/// so <see cref="Analyze"/> reports when an edit could silently drop formatting.
/// </summary>
public static partial class HtmlMarkdown
{
    private static readonly ReverseMarkdown.Converter ToMd = new(new ReverseMarkdown.Config
    {
        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.PassThrough,
        GithubFlavored = true,
        RemoveComments = true,
    });

    private static readonly MarkdownPipeline MdPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string ToMarkdown(string? html) =>
        string.IsNullOrEmpty(html) ? "" : ToMd.Convert(html).Trim();

    public static string ToHtml(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? "" : Markdown.ToHtml(markdown, MdPipeline).Trim();

    // Tags Markdown represents faithfully; anything else (tables, spans, styling) is lossy.
    private static readonly HashSet<string> MarkdownSafeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "b", "strong", "i", "em", "u", "s", "strike", "del", "a", "code", "pre",
        "ul", "ol", "li", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "div", "span",
    };

    // span/div are "safe" as bare wrappers but carry styling when they have attributes.
    private static readonly HashSet<string> StyleCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        "span", "div",
    };

    /// <summary>
    /// Converts to Markdown and flags when the source HTML uses features Markdown can't
    /// carry (tables, inline styles, unknown tags) — the warn-before-overwrite guard (SPEC §2).
    /// </summary>
    public static MarkdownAnalysis Analyze(string? html)
    {
        var markdown = ToMarkdown(html);
        if (string.IsNullOrEmpty(html))
        {
            return new MarkdownAnalysis(markdown, Lossy: false);
        }

        var lossy = false;
        foreach (Match m in TagRegex().Matches(html))
        {
            var name = m.Groups["name"].Value;
            var attrs = m.Groups["attrs"].Value;
            if (!MarkdownSafeTags.Contains(name))
            {
                lossy = true;
                break;
            }
            // Match the attribute name (style=), not any value containing "style"
            // (e.g. title="lifestyle guide" round-trips cleanly).
            if (StyleAttrRegex().IsMatch(attrs) ||
                (StyleCarriers.Contains(name) && ClassAttrRegex().IsMatch(attrs)))
            {
                lossy = true;
                break;
            }
        }

        return new MarkdownAnalysis(markdown, lossy);
    }

    [GeneratedRegex(@"<\s*/?\s*(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*)>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\bstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttrRegex();

    [GeneratedRegex(@"\bclass\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttrRegex();
}
