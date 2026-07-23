using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.Tests.Tasks;

public class PublishedTests
{
    private sealed record Snapshot(string Key, int Value);

    [Fact]
    public void Current_Is_Null_Initially_Publish_Sets_And_Null_Clears()
    {
        var published = new Published<Snapshot>();
        Assert.Null(published.Current);

        var snapshot = new Snapshot("a", 1);
        published.Publish(snapshot);
        Assert.Same(snapshot, published.Current);

        published.Publish(null);
        Assert.Null(published.Current);
    }
}
