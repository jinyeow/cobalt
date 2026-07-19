using Cobalt.Core.Ado;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class OperationLogTests
{
    private static AdoOperation Op(string name = "GET", string route = "_apis/x", int? status = 200) =>
        new(name, route, TimeSpan.FromMilliseconds(10), status, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Keeps_History_Newest_Last()
    {
        var log = new OperationLog();

        log.Add(Op(route: "_apis/one"));
        log.Add(Op(route: "_apis/two"));

        Assert.Equal(["_apis/one", "_apis/two"], log.History.Select(o => o.RouteShape));
    }

    [Fact]
    public void History_Is_Bounded()
    {
        var log = new OperationLog(capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            log.Add(Op(route: $"_apis/{i}"));
        }

        Assert.Equal(3, log.History.Count);
        Assert.Equal("_apis/9", log.History[^1].RouteShape);
    }

    [Fact]
    public void Raises_Changed_Event()
    {
        var log = new OperationLog();
        var raised = 0;
        log.Changed += () => raised++;

        log.Add(Op());

        Assert.Equal(1, raised);
    }
}
