using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class PolicyApiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private PolicyApi Api(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new PolicyApi(new AdoHttp(httpClient));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task GetEvaluations_Maps_DisplayName_Status_And_Blocking()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"value":[
              {"status":"approved","configuration":{"isBlocking":true,"type":{"displayName":"Minimum number of reviewers"}}},
              {"status":"rejected","configuration":{"isBlocking":true,"type":{"displayName":"Build validation"}}},
              {"status":"queued","configuration":{"isBlocking":false,"type":{"displayName":"Comment requirements"}}}
            ]}
            """);

        var evaluations = await Api(handler).GetEvaluationsAsync(
            "proj-guid", 42, TestContext.Current.CancellationToken);

        Assert.Equal(3, evaluations.Count);

        Assert.Equal("Minimum number of reviewers", evaluations[0].DisplayName);
        Assert.Equal("approved", evaluations[0].Status);
        Assert.True(evaluations[0].IsBlocking);

        Assert.Equal("Build validation", evaluations[1].DisplayName);
        Assert.Equal("rejected", evaluations[1].Status);
        Assert.True(evaluations[1].IsBlocking);

        Assert.Equal("Comment requirements", evaluations[2].DisplayName);
        Assert.Equal("queued", evaluations[2].Status);
        Assert.False(evaluations[2].IsBlocking);
    }

    [Fact]
    public async Task GetEvaluations_Builds_CodeReviewId_ArtifactId_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        await Api(handler).GetEvaluationsAsync("proj-guid", 42, TestContext.Current.CancellationToken);

        var uri = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("proj-guid/_apis/policy/evaluations", uri);
        Assert.Contains("artifactId=vstfs:///CodeReview/CodeReviewId/proj-guid/42", uri);
        Assert.Contains("api-version=7.2-preview.1", uri);
    }

    [Fact]
    public async Task GetEvaluations_Degrades_Missing_Configuration_To_Defaults()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"value":[{"status":"running"}]}""");

        var evaluations = await Api(handler).GetEvaluationsAsync(
            "proj-guid", 1, TestContext.Current.CancellationToken);

        Assert.Equal("policy", evaluations[0].DisplayName);
        Assert.Equal("running", evaluations[0].Status);
        Assert.False(evaluations[0].IsBlocking);
    }

    [Fact]
    public async Task GetEvaluations_Empty_When_No_Policies()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        var evaluations = await Api(handler).GetEvaluationsAsync(
            "proj-guid", 1, TestContext.Current.CancellationToken);

        Assert.Empty(evaluations);
    }
}
