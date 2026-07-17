using Cobalt.Tui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.App;

public class KeyTokenizerTests
{
    [Theory]
    [InlineData('j', "j")]
    [InlineData('k', "k")]
    [InlineData('G', "G")]
    [InlineData(':', ":")]
    [InlineData('?', "?")]
    [InlineData('/', "/")]
    [InlineData('1', "1")]
    public void Printable_Keys_Map_To_Themselves(char c, string expected)
    {
        Assert.Equal(expected, KeyTokenizer.ToToken(new Key(c)));
    }

    [Fact]
    public void Named_Keys()
    {
        Assert.Equal("Enter", KeyTokenizer.ToToken(Key.Enter));
        Assert.Equal("Esc", KeyTokenizer.ToToken(Key.Esc));
        Assert.Equal("Tab", KeyTokenizer.ToToken(Key.Tab));
        Assert.Equal("S-Tab", KeyTokenizer.ToToken(Key.Tab.WithShift));
    }

    [Fact]
    public void Control_Chords_Lowercase()
    {
        Assert.Equal("C-d", KeyTokenizer.ToToken(new Key('d').WithCtrl));
        Assert.Equal("C-u", KeyTokenizer.ToToken(new Key('u').WithCtrl));
        Assert.Equal("C-h", KeyTokenizer.ToToken(new Key('h').WithCtrl));
    }

    [Fact]
    public void Cursor_Keys_Map_To_Hjkl_Equivalents()
    {
        Assert.Equal("Up", KeyTokenizer.ToToken(Key.CursorUp));
        Assert.Equal("Down", KeyTokenizer.ToToken(Key.CursorDown));
        Assert.Equal("h", KeyTokenizer.ToToken(Key.CursorLeft));
        Assert.Equal("l", KeyTokenizer.ToToken(Key.CursorRight));
    }

    [Fact]
    public void Alt_Chords_Are_Ignored()
    {
        Assert.Null(KeyTokenizer.ToToken(new Key('x').WithAlt));
    }

    // ---- INPUT-3 review: modifier-matrix parity (guards the ctrl-branch masking) ----

    public static TheoryData<Key, string?> ModifierMatrix() => new()
    {
        // Ctrl+Alt (AltGr on European layouts) must fall through to Terminal.Gui, never fire a
        // vim command. Old behaviour: null.
        { new Key('d').WithCtrl.WithAlt, null },
        { new Key('x').WithCtrl.WithAlt, null },
        // Ctrl+Shift+letter is still a control chord (Shift is irrelevant to A-Z chords).
        { new Key('x').WithCtrl.WithShift, "C-x" },
        // Lowercase codepoint (0x61-0x7A) + Ctrl: GetIsKeyCodeAtoZ is true for these, so the
        // index must normalise to 0-25 rather than overrun the 26-element table.
        { new Key((KeyCode)'a' | KeyCode.CtrlMask), "C-a" },
        { new Key((KeyCode)'z' | KeyCode.CtrlMask), "C-z" },
        // Ctrl + non-letter (digit/punctuation) has no chord token → null.
        { new Key('1').WithCtrl, null },
        { new Key(',').WithCtrl, null },
        // Alt+letter alone is ignored.
        { new Key('a').WithAlt, null },
    };

    [Theory]
    [MemberData(nameof(ModifierMatrix))]
    public void Modifier_Combinations_Tokenize_As_Expected(Key key, string? expected)
    {
        Assert.Equal(expected, KeyTokenizer.ToToken(key));
    }

    // ---- INPUT-3: steady-state tokenizing shouldn't allocate ----

    [Fact]
    public void Steady_State_Ascii_Key_Allocates_Nothing()
    {
        var key = new Key('j');
        KeyTokenizer.ToToken(key); // warm up JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            KeyTokenizer.ToToken(key);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"steady-state ASCII tokenizing must not allocate, but allocated {allocated} bytes");
    }

    [Fact]
    public void Steady_State_Control_Chord_Allocates_Nothing()
    {
        var key = new Key('d').WithCtrl;
        KeyTokenizer.ToToken(key); // warm up JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            KeyTokenizer.ToToken(key);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"steady-state control-chord tokenizing must not allocate, but allocated {allocated} bytes");
    }
}
