using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class GitApiTests : IDisposable
{
    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "My Project",
    };

    private static readonly Guid Me = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly List<IDisposable> _disposables = [];

    private GitApi Api(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new GitApi(new AdoHttp(httpClient), Context);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task ListActive_Queries_Project_Scoped_Endpoint()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"value":[
              {"pullRequestId":10,"title":"Add feature","status":"active","isDraft":false,
               "sourceRefName":"refs/heads/feature","targetRefName":"refs/heads/main",
               "createdBy":{"displayName":"Jin"},"repository":{"id":"repo-1","name":"web"},
               "reviewers":[{"displayName":"Sam","id":"r1","vote":10}]}
            ]}
            """);

        var prs = await Api(handler).ListPullRequestsAsync(
            PrListFilter.Active, Me, TestContext.Current.CancellationToken);

        Assert.Single(prs);
        Assert.Equal(10, prs[0].PullRequestId);
        Assert.Equal("Add feature", prs[0].Title);
        Assert.Equal("web", prs[0].RepositoryName);
        Assert.Equal("feature", prs[0].SourceBranch);
        Assert.Equal("main", prs[0].TargetBranch);
        Assert.Equal(PrVote.Approved, prs[0].Reviewers[0].Vote);

        var uri = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("My Project/_apis/git/pullrequests", uri);
        Assert.Contains("searchCriteria.status=active", uri);
    }

    [Fact]
    public async Task ReviewQueue_Filters_By_ReviewerId()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        await Api(handler).ListPullRequestsAsync(PrListFilter.ReviewQueue, Me, TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains($"searchCriteria.reviewerId={Me}", uri);
    }

    [Fact]
    public async Task Mine_Filters_By_CreatorId()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        await Api(handler).ListPullRequestsAsync(PrListFilter.Mine, Me, TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains($"searchCriteria.creatorId={Me}", uri);
    }

    [Fact]
    public async Task GetPullRequest_Uses_OrgLevel_ById_Endpoint()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"pullRequestId":10,"title":"Add feature","description":"body","status":"active",
             "sourceRefName":"refs/heads/feature","targetRefName":"refs/heads/main","mergeStatus":"succeeded",
             "createdBy":{"displayName":"Jin"},"repository":{"id":"repo-1","name":"web"},"reviewers":[]}
            """);

        var pr = await Api(handler).GetPullRequestAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal("body", pr.Description);
        Assert.Equal("succeeded", pr.MergeStatus);
        Assert.Contains("_apis/git/pullrequests/10", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetThreads_Parses_Comments_And_Status()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"value":[
              {"id":1,"status":"active","comments":[
                {"id":1,"content":"please fix","author":{"displayName":"Sam"},"commentType":"text"}]},
              {"id":2,"status":null,"comments":[
                {"id":1,"content":"system","author":{"displayName":"x"},"commentType":"system"}]}
            ]}
            """);

        var threads = await Api(handler).GetThreadsAsync("repo-1", 10, TestContext.Current.CancellationToken);

        // system-only threads are filtered out
        Assert.Single(threads);
        Assert.Equal("please fix", threads[0].Comments[0].Content);
        Assert.Equal(PrThreadStatus.Active, threads[0].Status);
    }

    [Fact]
    public async Task Vote_Puts_Reviewer_With_Numeric_Value()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"displayName":"Jin","id":"me","vote":10}""");

        await Api(handler).VoteAsync("repo-1", 10, Me, PrVote.Approved, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains($"reviewers/{Me}", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("\"vote\":10", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task ReplyToThread_Posts_Comment()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":2,"content":"ok","author":{"displayName":"Jin"},"commentType":"text"}""");

        await Api(handler).ReplyToThreadAsync("repo-1", 10, threadId: 3, "ok thanks", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("threads/3/comments", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("ok thanks", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task SetThreadStatus_Patches_Thread()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"id":3,"status":"fixed"}""");

        await Api(handler).SetThreadStatusAsync("repo-1", 10, 3, PrThreadStatus.Fixed, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("threads/3", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("fixed", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Abandon_Patches_Status()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"pullRequestId":10,"title":"x","status":"abandoned","sourceRefName":"refs/heads/f","targetRefName":"refs/heads/main","repository":{"id":"r","name":"web"},"reviewers":[]}""");

        await Api(handler).AbandonAsync("repo-1", 10, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("\"status\":\"abandoned\"", handler.RequestBodies[0]);
    }
}
