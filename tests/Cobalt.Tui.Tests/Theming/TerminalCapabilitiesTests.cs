using Cobalt.Tui.Theming;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// Pure, table-driven guards for <see cref="TerminalCapabilities.Detect"/>. Detection is decided
/// deterministically from an injected env lookup (the same <c>Func&lt;string, string?&gt;</c> seam
/// as <c>CobaltTuiApp.ResolveDriver</c>, ADR 0016) and never probes the live terminal, so every
/// tier is reachable from a plain unit test.
/// </summary>
public class TerminalCapabilitiesTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    [Fact]
    public void No_Color_Any_Nonempty_Value_Forces_Mono()
    {
        var caps = TerminalCapabilities.Detect(Env(("NO_COLOR", "1"), ("COLORTERM", "truecolor")));

        Assert.Equal(ColorSupport.None, caps.Color);
    }

    [Fact]
    public void Empty_No_Color_Is_Ignored_Per_Spec()
    {
        var caps = TerminalCapabilities.Detect(Env(("NO_COLOR", ""), ("COLORTERM", "truecolor")));

        Assert.Equal(ColorSupport.Full, caps.Color);
    }

    [Fact]
    public void ColorTerm_Truecolor_Yields_Full()
    {
        var caps = TerminalCapabilities.Detect(Env(("COLORTERM", "truecolor")));

        Assert.Equal(ColorSupport.Full, caps.Color);
    }

    [Fact]
    public void Wt_Session_Advertises_Full_Without_ColorTerm()
    {
        var caps = TerminalCapabilities.Detect(Env(("WT_SESSION", "abc-123")));

        Assert.Equal(ColorSupport.Full, caps.Color);
    }

    [Fact]
    public void Term_With_256color_Yields_Full()
    {
        var caps = TerminalCapabilities.Detect(Env(("TERM", "xterm-256color")));

        Assert.Equal(ColorSupport.Full, caps.Color);
    }

    [Fact]
    public void Named_Term_Without_Rich_Signal_Falls_Back_To_Ansi16()
    {
        var caps = TerminalCapabilities.Detect(Env(("TERM", "xterm-16color")));

        Assert.Equal(ColorSupport.Ansi16, caps.Color);
    }

    [Fact]
    public void Dumb_Terminal_Is_Mono_And_Not_Unicode_Safe()
    {
        var caps = TerminalCapabilities.Detect(Env(("TERM", "dumb")));

        Assert.Equal(ColorSupport.None, caps.Color);
        Assert.False(caps.UnicodeSafe);
    }

    [Fact]
    public void Cobalt_Color_Override_Beats_Heuristics()
    {
        var caps = TerminalCapabilities.Detect(Env(("COBALT_COLOR", "16"), ("COLORTERM", "truecolor")));

        Assert.Equal(ColorSupport.Ansi16, caps.Color);
    }

    [Fact]
    public void No_Color_Beats_Cobalt_Color_Override()
    {
        var caps = TerminalCapabilities.Detect(Env(("NO_COLOR", "1"), ("COBALT_COLOR", "true")));

        Assert.Equal(ColorSupport.None, caps.Color);
    }

    [Fact]
    public void Linux_Console_Is_Not_Unicode_Safe_But_Keeps_Ansi16()
    {
        var caps = TerminalCapabilities.Detect(Env(("TERM", "linux")));

        Assert.Equal(ColorSupport.Ansi16, caps.Color);
        Assert.False(caps.UnicodeSafe);
    }

    [Fact]
    public void A_Normal_Truecolor_Terminal_Is_Unicode_Safe()
    {
        var caps = TerminalCapabilities.Detect(Env(("COLORTERM", "truecolor"), ("TERM", "xterm-256color")));

        Assert.True(caps.UnicodeSafe);
    }

    [Fact]
    public void Empty_Environment_Keeps_Ansi16_Not_Mono()
    {
        // A missing TERM is the common Windows-conhost case; blanking colour there would be worse
        // than assuming the 16 ANSI colours every terminal has. Only NO_COLOR/dumb force mono.
        var caps = TerminalCapabilities.Detect(_ => null);

        Assert.Equal(ColorSupport.Ansi16, caps.Color);
        Assert.True(caps.UnicodeSafe);
    }

    [Theory]
    [InlineData("iTerm.app")]
    [InlineData("WezTerm")]
    [InlineData("vscode")]
    public void Known_Truecolor_Term_Program_Yields_Full(string program)
    {
        var caps = TerminalCapabilities.Detect(Env(("TERM_PROGRAM", program)));

        Assert.Equal(ColorSupport.Full, caps.Color);
    }

    [Theory]
    [InlineData("full", ColorSupport.Full)]
    [InlineData("truecolor", ColorSupport.Full)]
    [InlineData("16", ColorSupport.Ansi16)]
    [InlineData("none", ColorSupport.None)]
    public void Cobalt_Color_Override_Parses_Every_Documented_Value(string value, ColorSupport expected)
    {
        var caps = TerminalCapabilities.Detect(Env(("COBALT_COLOR", value)));

        Assert.Equal(expected, caps.Color);
    }

    [Fact]
    public void Unparseable_Cobalt_Color_Throws_Rather_Than_Silently_Ignoring()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TerminalCapabilities.Detect(Env(("COBALT_COLOR", "purple"))));

        Assert.Contains("COBALT_COLOR", ex.Message);
        Assert.Contains("purple", ex.Message);
    }
}
