using Cobalt.Tui.App;
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
