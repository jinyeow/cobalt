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
    public static string? ToToken(Key key)
    {
        // Match named keys on the base code, ignoring modifier bits OR'd into KeyCode.
        switch (key.NoShift.NoAlt.NoCtrl.KeyCode)
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

        if (key.IsCtrl)
        {
            var plain = key.NoCtrl.NoShift;
            if (plain.IsKeyCodeAtoZ)
            {
                return $"C-{char.ToLowerInvariant((char)plain.AsRune.Value)}";
            }
            return null;
        }

        if (key.IsAlt)
        {
            return null;
        }

        return key.TryGetPrintableRune(out var rune) ? rune.ToString() : null;
    }
}
