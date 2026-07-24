using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class PreviewModesTests
{
    [Fact]
    public void Names_Are_The_Lowercased_Vocabulary_In_Declaration_Order()
    {
        Assert.Equal(["auto", "off"], PreviewModes.Names);
    }

    [Theory]
    [InlineData("auto", PreviewMode.Auto)]
    [InlineData("OFF", PreviewMode.Off)]
    [InlineData("Auto", PreviewMode.Auto)]
    public void TryParse_Accepts_Each_Name_Case_Insensitively(string name, PreviewMode expected)
    {
        Assert.True(PreviewModes.TryParse(name, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("auto,off")] // Enum.TryParse's flags-style combination
    [InlineData("0")]        // Enum.TryParse's numeric form
    [InlineData("on")]
    [InlineData("")]
    public void TryParse_Rejects_Anything_But_An_Exact_Name(string name)
    {
        Assert.False(PreviewModes.TryParse(name, out _));
    }
}
