namespace Cobalt.Tui.Theming;

/// <summary>
/// The colour depth a terminal can render, from richest to poorest. Ordered so the tiers compare
/// (<c>None &lt; Ansi16 &lt; Full</c>): startup couples Terminal.Gui's <c>Force16Colors</c> to
/// <c>Color &lt; Full</c> so the chrome degrades in step with the diff (ADR 0019).
/// </summary>
public enum ColorSupport
{
    /// <summary>No colour — monochrome. The diff conveys add/remove by sign-only gutters.</summary>
    None,

    /// <summary>The 16 ANSI colours only (<see cref="Terminal.Gui.Drawing.ColorName16"/>).</summary>
    Ansi16,

    /// <summary>24-bit truecolor — the full RGB palette cobalt ships.</summary>
    Full,
}

/// <summary>
/// The colour depth and Unicode-width support cobalt should assume for the current terminal,
/// decided <em>deterministically from environment variables</em> and never by probing the live
/// terminal. Detection uses the same injected <c>Func&lt;string, string?&gt;</c> env seam as
/// <c>CobaltTuiApp.ResolveDriver</c> (ADR 0016), so it is a pure function and fully unit-testable.
/// Published once at startup as <c>ThemeService.Capabilities</c> and consumed by the
/// colour-degradation tiers (ADR 0019 extension).
/// </summary>
public sealed record TerminalCapabilities(ColorSupport Color, bool UnicodeSafe)
{
    /// <summary>
    /// Decides the terminal's <see cref="ColorSupport"/> and <see cref="UnicodeSafe"/> from
    /// <paramref name="getEnv"/>. Precedence (first match wins):
    /// <list type="number">
    /// <item><c>NO_COLOR</c> set to any non-empty value → <see cref="ColorSupport.None"/>
    ///   (the standard opt-out; an empty value is ignored per the NO_COLOR spec).</item>
    /// <item><c>COBALT_COLOR</c> explicit override: <c>none</c>/<c>0</c> → None, <c>16</c> →
    ///   <see cref="ColorSupport.Ansi16"/>, <c>true</c>/<c>truecolor</c>/<c>24bit</c> →
    ///   <see cref="ColorSupport.Full"/>.</item>
    /// <item><c>TERM=dumb</c> → None.</item>
    /// <item><c>COLORTERM=truecolor</c>/<c>24bit</c>, or a non-empty <c>WT_SESSION</c>
    ///   (Windows Terminal), or <c>TERM</c> containing <c>truecolor</c>/<c>24bit</c>/<c>256color</c>
    ///   → Full.</item>
    /// <item>otherwise → Ansi16.</item>
    /// </list>
    /// <see cref="UnicodeSafe"/> is <c>false</c> for the Linux console (<c>TERM=linux</c>) and a
    /// dumb terminal, which lack box-drawing/wide-glyph coverage; otherwise <c>true</c>. It is
    /// detected and exposed now, consumed by a later renderer change (ADR 0019 extension).
    /// </summary>
    public static TerminalCapabilities Detect(Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);

        var term = getEnv("TERM")?.Trim() ?? string.Empty;
        var unicodeSafe = !term.Equals("linux", StringComparison.OrdinalIgnoreCase)
                          && !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);

        return new TerminalCapabilities(DetectColor(getEnv, term), unicodeSafe);
    }

    private static ColorSupport DetectColor(Func<string, string?> getEnv, string term)
    {
        // NO_COLOR: any non-empty value disables colour; an empty value is explicitly ignored.
        if (!string.IsNullOrEmpty(getEnv("NO_COLOR")))
        {
            return ColorSupport.None;
        }

        // Explicit app override beats every heuristic below; an unparseable value is a config
        // error, so fail loud with the offending value named rather than silently ignoring it.
        var overridden = getEnv("COBALT_COLOR")?.Trim();
        if (!string.IsNullOrEmpty(overridden))
        {
            return overridden.ToLowerInvariant() switch
            {
                "none" or "0" or "mono" => ColorSupport.None,
                "16" or "ansi16" => ColorSupport.Ansi16,
                "true" or "truecolor" or "24bit" or "full" => ColorSupport.Full,
                _ => throw new ArgumentException(
                    $"COBALT_COLOR='{overridden}' is not a recognised value; use none, 16, or true.",
                    nameof(getEnv)),
            };
        }

        if (term.Equals("dumb", StringComparison.OrdinalIgnoreCase))
        {
            return ColorSupport.None;
        }

        var colorTerm = getEnv("COLORTERM")?.Trim().ToLowerInvariant();
        if (colorTerm is "truecolor" or "24bit")
        {
            return ColorSupport.Full;
        }

        // Windows Terminal advertises truecolor via WT_SESSION, and some emulators via TERM_PROGRAM
        // rather than COLORTERM.
        if (!string.IsNullOrEmpty(getEnv("WT_SESSION")))
        {
            return ColorSupport.Full;
        }

        var termProgram = getEnv("TERM_PROGRAM")?.Trim();
        if (termProgram is not null && KnownTruecolorPrograms.Contains(termProgram))
        {
            return ColorSupport.Full;
        }

        if (term.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || term.Contains("24bit", StringComparison.OrdinalIgnoreCase)
            || term.Contains("256color", StringComparison.OrdinalIgnoreCase))
        {
            return ColorSupport.Full;
        }

        // A terminal that names itself (or none — the common Windows conhost case) but signals no
        // rich colour still gets the 16 ANSI colours; only NO_COLOR and TERM=dumb blank it.
        return ColorSupport.Ansi16;
    }

    // Emulators that render truecolor but advertise it only through TERM_PROGRAM (not COLORTERM).
    private static readonly HashSet<string> KnownTruecolorPrograms =
        new(StringComparer.OrdinalIgnoreCase) { "iTerm.app", "WezTerm", "vscode" };
}
