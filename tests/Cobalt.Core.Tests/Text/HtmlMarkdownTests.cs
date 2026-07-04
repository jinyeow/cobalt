using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

public class HtmlMarkdownTests
{
    [Fact]
    public void Html_To_Markdown_Basic_Formatting()
    {
        var md = HtmlMarkdown.ToMarkdown("<p>Hello <b>world</b></p>");

        Assert.Contains("Hello", md);
        Assert.Contains("**world**", md);
    }

    [Fact]
    public void Markdown_To_Html_Basic_Formatting()
    {
        var html = HtmlMarkdown.ToHtml("Hello **world**");

        Assert.Contains("<strong>world</strong>", html);
    }

    [Fact]
    public void Empty_And_Null_Html_Become_Empty_Markdown()
    {
        Assert.Equal("", HtmlMarkdown.ToMarkdown(null));
        Assert.Equal("", HtmlMarkdown.ToMarkdown(""));
    }

    [Fact]
    public void Simple_Content_Round_Trips_Cleanly()
    {
        var result = HtmlMarkdown.Analyze("<p>A simple <i>description</i> with a <a href=\"http://x\">link</a>.</p>");

        Assert.False(result.Lossy);
        Assert.Contains("description", result.Markdown);
    }

    [Fact]
    public void Complex_Html_Is_Flagged_Lossy()
    {
        // Inline styles / tables don't survive the HTML->MD->HTML round trip.
        var html = "<table style=\"color:red\"><tr><td>cell</td></tr></table><div style=\"font-weight:bold\">x</div>";
        var result = HtmlMarkdown.Analyze(html);

        Assert.True(result.Lossy);
    }

    [Fact]
    public void Lossy_Analysis_Preserves_Text_Content_In_Markdown()
    {
        var result = HtmlMarkdown.Analyze("<div>important note</div>");

        Assert.Contains("important note", result.Markdown);
    }

    [Fact]
    public void Attribute_Value_Containing_Style_Is_Not_A_False_Positive()
    {
        // "lifestyle" in a title must not trip the style= detector.
        var result = HtmlMarkdown.Analyze("<p><a href=\"/x\" title=\"lifestyle guide\">link</a></p>");

        Assert.False(result.Lossy);
    }

    [Fact]
    public void Actual_Style_Attribute_Is_Flagged()
    {
        var result = HtmlMarkdown.Analyze("<p style=\"color:red\">warn</p>");

        Assert.True(result.Lossy);
    }
}
