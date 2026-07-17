using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class WorkItemStoreAdapterTests : IDisposable
{
    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
    }

    /// <summary>Counts each states call; the first response can be scripted to fault.</summary>
    private sealed class StatesHandler(string okJson, bool firstFaults = false, bool firstCancels = false)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref _calls);
            if (firstCancels && n == 1)
            {
                throw new TaskCanceledException("simulated HttpClient timeout");
            }
            if (firstFaults && n == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"message":"boom"}""", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>Counts states calls and gates the first response so overlapping callers stay in flight.</summary>
    private sealed class GatedStatesHandler(string okJson) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private WorkItemStoreAdapter Adapter(StatesHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new WorkItemStoreAdapter(new WorkItemsApi(new AdoHttp(httpClient), Context));
    }

    private const string StatesJson = """{"value":[{"name":"New","category":"Proposed","color":"b2b2b2"}]}""";

    [Fact]
    public async Task GetStates_Caches_Per_Project_And_Type()
    {
        var handler = new StatesHandler(StatesJson);
        var adapter = Adapter(handler);

        var first = await adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);
        var second = await adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);

        Assert.Equal(["New"], second.Select(s => s.Name));
        Assert.Same(first, second); // same cached list instance served both calls
        Assert.Equal(1, handler.Calls); // states fetched once for (Proj, Bug)
    }

    [Fact]
    public async Task GetStates_Fetches_Separately_For_A_Different_Type()
    {
        var handler = new StatesHandler(StatesJson);
        var adapter = Adapter(handler);

        await adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);
        await adapter.GetStatesAsync("Task", "Proj", TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls); // (Proj,Bug) and (Proj,Task) are distinct keys
    }

    [Fact]
    public async Task GetStates_Retries_After_A_Faulted_Fetch()
    {
        var handler = new StatesHandler(StatesJson, firstFaults: true);
        var adapter = Adapter(handler);

        await Assert.ThrowsAsync<AdoApiException>(() =>
            adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken));
        var states = await adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);

        Assert.Equal(["New"], states.Select(s => s.Name));
        Assert.Equal(2, handler.Calls); // faulted attempt evicted, retry re-fetched
    }

    [Fact]
    public async Task GetStates_Retries_After_A_Canceled_Fetch()
    {
        // An HttpClient timeout surfaces as a canceled task, not a faulted one; it must still be
        // evicted so the state-change dialog isn't stuck rethrowing a stale cancellation all session.
        var handler = new StatesHandler(StatesJson, firstCancels: true);
        var adapter = Adapter(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken));
        var states = await adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);

        Assert.Equal(["New"], states.Select(s => s.Name));
        Assert.Equal(2, handler.Calls); // canceled attempt evicted, retry re-fetched
    }

    [Fact]
    public async Task GetStates_Treats_Null_Project_As_The_Context_Project()
    {
        // api.GetStatesAsync(null) resolves to the context project, so a null-project call and an
        // explicit context-project call hit the same endpoint and must share one cache entry.
        var handler = new StatesHandler(StatesJson);
        var adapter = Adapter(handler);

        await adapter.GetStatesAsync("Bug", null, TestContext.Current.CancellationToken);
        await adapter.GetStatesAsync("Bug", Context.Project, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls); // ("", Bug) and ("Proj", Bug) collapse to one key
    }

    [Fact]
    public async Task GetStates_Collapses_Overlapping_Callers_Into_One_Fetch()
    {
        var handler = new GatedStatesHandler(StatesJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        var adapter = new WorkItemStoreAdapter(new WorkItemsApi(new AdoHttp(httpClient), Context));

        // Both callers arrive while the first fetch is still gated in flight.
        var a = adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);
        var b = adapter.GetStatesAsync("Bug", "Proj", TestContext.Current.CancellationToken);
        handler.Release();
        await Task.WhenAll(a, b);

        Assert.Equal(1, handler.Calls); // single-flight: one fetch served both overlapping callers
    }
}
