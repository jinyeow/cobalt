using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class ThemeChoicesTests
{
    [Fact]
    public void Names_Cover_Every_Enum_Member_Lowercased_In_Order()
    {
        Assert.Equal(
            Enum.GetNames<ThemeChoice>().Select(n => n.ToLowerInvariant()),
            ThemeChoices.Names);
    }

    [Theory]
    [InlineData("dark", ThemeChoice.Dark)]
    [InlineData("LIGHT", ThemeChoice.Light)]
    [InlineData("System", ThemeChoice.System)]
    public void TryParse_Accepts_Each_Name_Case_Insensitively(string name, ThemeChoice expected)
    {
        Assert.True(ThemeChoices.TryParse(name, out var choice));
        Assert.Equal(expected, choice);
    }

    [Theory]
    [InlineData("dark,light")] // Enum.TryParse's flags-style combination
    [InlineData("0")]          // Enum.TryParse's numeric form
    [InlineData("rainbow")]
    [InlineData("")]
    public void TryParse_Rejects_Anything_But_An_Exact_Name(string name)
    {
        Assert.False(ThemeChoices.TryParse(name, out _));
    }
}
