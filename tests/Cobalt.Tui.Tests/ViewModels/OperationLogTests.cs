using Cobalt.Core.Ado;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class OperationLogTests
{
    private static AdoOperation Op(string name = "GET", string route = "_apis/x", int? status = 200) =>
        AdoOperation.FromRoute(name, route, TimeSpan.FromMilliseconds(10), status, DateTimeOffset.UnixEpoch);

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
            log.Add(Op(route: $"_apis/route{i}"));
        }

        Assert.Equal(3, log.History.Count);
        Assert.Equal("_apis/route9", log.History[^1].RouteShape);
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

    [Fact]
    public void Concurrent_Adds_From_Many_Threads_Do_Not_Throw_Or_Corrupt_History()
    {
        // The observer fires on threadpool continuation threads (ConfigureAwait(false) inside
        // AdoHttp), so concurrent Add calls are a real scenario, not a hypothetical.
        var log = new OperationLog(capacity: 1000);
        const int perThread = 50;
        const int threads = 8;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                log.Add(Op(route: $"_apis/t{t}-{i}"));
            }
        });

        Assert.Equal(threads * perThread, log.History.Count);
    }
}
