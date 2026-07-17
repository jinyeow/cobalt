using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Cobalt.Tui.App;

/// <summary>
/// Converts Terminal.Gui key events into the plain tokens the KeymapRouter
/// understands ("j", "G", "C-d", "Enter", "S-Tab"). The only place where
/// Terminal.Gui input types meet the vim layer.
/// </summary>
public static class KeyTokenizer
{
    private const KeyCode ModifierMask = KeyCode.ShiftMask | KeyCode.AltMask | KeyCode.CtrlMask;

    // INPUT-3: precomputed so a steady-state keystroke never allocates a token string.
    private static readonly string[] CtrlTokens = BuildCtrlTokens();
    private static readonly string[] AsciiTokens = BuildAsciiTokens();

    public static string? ToToken(Key key)
    {
        // Mask the modifier bits directly on the KeyCode value instead of chaining
        // Key.NoShift/.NoAlt/.NoCtrl — each of those allocates a new Key object (it's a
        // class, not a struct), which a per-keystroke hot path can't afford.
        var code = key.KeyCode & ~ModifierMask;

        switch (code)
        {
            case KeyCode.Esc:
                return "Esc";
            case KeyCode.Enter:
                return "Enter";
            case KeyCode.Tab:
                return key.IsShift ? "S-Tab" : "Tab";
            case KeyCode.CursorUp:
                return "Up";
            case KeyCode.CursorDown:
                return "Down";
            case KeyCode.CursorLeft:
                return "h";
            case KeyCode.CursorRight:
                return "l";
            default:
                break;
        }

        // Ctrl (but not Ctrl+Alt): a control chord. Ctrl+Alt is AltGr on European layouts —
        // it produces printable characters, so it must fall through to the IsAlt guard below
        // (returns null → Terminal.Gui handles it) rather than fire a vim command.
        if (key.IsCtrl && !key.IsAlt)
        {
            // GetIsKeyCodeAtoZ is true for BOTH the A-Z codes (0x41-0x5A) and lowercase
            // codepoints (0x61-0x7A), so fold to lowercase before indexing to keep the
            // offset in 0-25 (a raw subtract from KeyCode.A overruns the table for 0x61+).
            return Key.GetIsKeyCodeAtoZ(code) ? CtrlTokens[((int)code | 0x20) - 'a'] : null;
        }

        if (key.IsAlt)
        {
            return null;
        }

        if (!key.TryGetPrintableRune(out var rune))
        {
            return null;
        }

        var value = rune.Value;
        return value is >= 0x20 and < 0x7F ? AsciiTokens[value - 0x20] : rune.ToString();
    }

    private static string[] BuildCtrlTokens()
    {
        var tokens = new string[26];
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = $"C-{(char)('a' + i)}";
        }
        return tokens;
    }

    private static string[] BuildAsciiTokens()
    {
        var tokens = new string[0x7F - 0x20];
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = ((char)(0x20 + i)).ToString();
        }
        return tokens;
    }
}
