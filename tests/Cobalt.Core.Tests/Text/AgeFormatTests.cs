using Cobalt.Core.Text;

namespace Cobalt.Core.Tests.Text;

public class AgeFormatTests
{
    public static TheoryData<int, string> Cases => new()
    {
        // minutes (< 1h)
        { 45 * 60, "45m" },
        { 59 * 60, "59m" },
        { 0, "0m" },
        // hours (< 24h)
        { 60 * 60, "1h" },            // exact 1h boundary rolls over to hours
        { 6 * 60 * 60, "6h" },
        { 23 * 60 * 60, "23h" },
        // days (< 14d)
        { 24 * 60 * 60, "1d" },       // exact 24h boundary rolls over to days
        { 3 * 24 * 60 * 60, "3d" },
        { 13 * 24 * 60 * 60, "13d" },
        // weeks (>= 14d)
        { 14 * 24 * 60 * 60, "2w" },  // exact 14d boundary rolls over to weeks
        { 5 * 7 * 24 * 60 * 60, "5w" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Short_Formats_Compactly(int seconds, string expected) =>
        Assert.Equal(expected, AgeFormat.Short(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Short_Clamps_Negative_To_Zero() =>
        Assert.Equal("0m", AgeFormat.Short(TimeSpan.FromSeconds(-30)));

    [Fact]
    public void Since_Null_CreationDate_Is_Dash() =>
        Assert.Equal("-", AgeFormat.Since(null, DateTimeOffset.UtcNow));

    [Fact]
    public void Since_Computes_Age_From_Now()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var created = now - TimeSpan.FromDays(3);
        Assert.Equal("3d", AgeFormat.Since(created, now));
    }
}
