using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Status line = shell status text left, pending-key showcmd right-aligned
/// (spec: lazygit-inspired redesign, stage A). Pure composition.
/// </summary>
public class StatusLineComposerTests
{
    [Fact]
    public void No_Pending_Returns_The_Left_Text_Unchanged()
    {
        Assert.Equal(" context:work", StatusLineComposer.Compose(" context:work", "", 80));
    }

    [Fact]
    public void Pending_Is_Right_Aligned_Within_The_Width()
    {
        var line = StatusLineComposer.Compose(" context:work", "5g", 40);

        Assert.Equal(40, line.Length);
        Assert.StartsWith(" context:work", line);
        Assert.EndsWith("5g ", line);
    }

    [Fact]
    public void Long_Left_Text_Is_Truncated_To_Keep_The_Pending_Visible()
    {
        var left = new string('x', 60);
        var line = StatusLineComposer.Compose(left, "12", 40);

        Assert.Equal(40, line.Length);
        Assert.EndsWith("12 ", line);
    }

    [Fact]
    public void Tiny_Width_Never_Throws_Or_Overflows()
    {
        var line = StatusLineComposer.Compose(" context:work", "999", 3);

        Assert.True(line.Length <= 3);
    }
}
