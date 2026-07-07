using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrReviewQueueFilterTests : IDisposable
{
    private static readonly Guid Me = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    private readonly List<IDisposable> _disposables = [];

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private PullRequestStoreAdapter Adapter(string json)
    {
        var httpClient = new HttpClient(new JsonHandler(json)) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        var api = new GitApi(new AdoHttp(httpClient), Context);
        return new PullRequestStoreAdapter(api, _ => Task.FromResult(Me));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task ReviewQueue_Drops_Prs_I_Already_Voted_On()
    {
        // PR 1: my vote pending (0) → keep. PR 2: I approved (10) → drop.
        var json =
            $$"""
            {"value":[
              {"pullRequestId":1,"title":"pending","status":"active","sourceRefName":"refs/heads/a","targetRefName":"refs/heads/main",
               "repository":{"id":"r","name":"web"},"reviewers":[{"id":"{{Me}}","displayName":"me","vote":0}]},
              {"pullRequestId":2,"title":"done","status":"active","sourceRefName":"refs/heads/b","targetRefName":"refs/heads/main",
               "repository":{"id":"r","name":"web"},"reviewers":[{"id":"{{Me}}","displayName":"me","vote":10}]}
            ]}
            """;

        var prs = await Adapter(json).ListPullRequestsAsync(
            PrListFilter.ReviewQueue, TestContext.Current.CancellationToken);

        Assert.Single(prs);
        Assert.Equal(1, prs[0].PullRequestId);
    }

    [Fact]
    public async Task Active_Tab_Is_Not_Filtered_By_Vote()
    {
        var json =
            $$"""
            {"value":[
              {"pullRequestId":2,"title":"done","status":"active","sourceRefName":"refs/heads/b","targetRefName":"refs/heads/main",
               "repository":{"id":"r","name":"web"},"reviewers":[{"id":"{{Me}}","displayName":"me","vote":10}]}
            ]}
            """;

        var prs = await Adapter(json).ListPullRequestsAsync(
            PrListFilter.Active, TestContext.Current.CancellationToken);

        Assert.Single(prs); // vote filter does not apply to Active
    }
}
