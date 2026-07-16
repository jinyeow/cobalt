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
    private sealed class StatesHandler(string okJson, bool firstFaults = false) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var n = Interlocked.Increment(ref _calls);
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
}
