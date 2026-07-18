using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class PrTeamFilterTests : IDisposable
{
    private static readonly Guid Me = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private const string TeammateId = "bbbbbbbb-0000-0000-0000-00000000000b";

    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

    private readonly List<IDisposable> _disposables = [];

    /// <summary>Dispatches PR-list GETs by query: reviewerId → team-reviewed set, else the active set.</summary>
    private sealed class DispatchHandler(string reviewerJson, string activeJson) : HttpMessageHandler
    {
        public int ActiveCalls { get; private set; }
        public int ReviewerCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            var query = request.RequestUri!.Query;
            var json = query.Contains("reviewerId=", StringComparison.Ordinal) ? reviewerJson : activeJson;
            if (query.Contains("reviewerId=", StringComparison.Ordinal))
            {
                ReviewerCalls++;
            }
            else
            {
                ActiveCalls++;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Pr(int id, string authorId, string date, Guid? teamReviewer = null)
    {
        var reviewers = teamReviewer is { } t ? $$"""[{"id":"{{t}}","displayName":"team","vote":0}]""" : "[]";
        return $$"""
            {"pullRequestId":{{id}},"title":"pr{{id}}","status":"active",
             "sourceRefName":"refs/heads/f","targetRefName":"refs/heads/main",
             "creationDate":"{{date}}","createdBy":{"id":"{{authorId}}","displayName":"a"},
             "repository":{"id":"r","name":"web","project":{"id":"p","name":"Proj"} },
             "reviewers":{{reviewers}} }
            """;
    }

    private PullRequestStoreAdapter Adapter(
        DispatchHandler handler,
        Func<CancellationToken, Task<TeamDirectory>> resolveTeams,
        PrScope scope = PrScope.Org)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        var api = new GitApi(new AdoHttp(httpClient), Context);
        return new PullRequestStoreAdapter(api, _ => Task.FromResult(Me), resolveTeams, Context.Project, scope);
    }

    private static Func<CancellationToken, Task<TeamDirectory>> Directory(
        params TeamMembership[] teams) => _ => Task.FromResult(new TeamDirectory(teams));

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task Team_Is_Raw_Union_Of_Reviewer_And_Author_Halves()
    {
        // Reviewer half: PR 1 (team is a reviewer). Author half: PR 2 (teammate-authored).
        var reviewer = $$"""{"value":[{{Pr(1, "someone", "2026-01-01T00:00:00Z", TeamId)}}]}""";
        var active = $$"""
            {"value":[
              {{Pr(2, TeammateId, "2026-01-02T00:00:00Z")}},
              {{Pr(3, "stranger", "2026-01-03T00:00:00Z")}}
            ]}
            """;
        var handler = new DispatchHandler(reviewer, active);
        var adapter = Adapter(handler, Directory(
            new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })));

        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        // PR1 (team reviewer) and PR2 (teammate author); PR3 (stranger author) excluded.
        Assert.Equal(new[] { 1, 2 }, prs.Select(p => p.PullRequestId).Order());
    }

    [Fact]
    public async Task Dedupes_Pr_That_Is_Both_Team_Reviewed_And_Teammate_Authored()
    {
        // PR 1 appears in BOTH halves: team reviewer AND teammate author.
        var reviewer = $$"""{"value":[{{Pr(1, TeammateId, "2026-01-01T00:00:00Z", TeamId)}}]}""";
        var active = $$"""{"value":[{{Pr(1, TeammateId, "2026-01-01T00:00:00Z")}}]}""";
        var handler = new DispatchHandler(reviewer, active);
        var adapter = Adapter(handler, Directory(
            new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })));

        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Single(prs);
        Assert.Equal(1, prs[0].PullRequestId);
    }

    [Fact]
    public async Task Does_Not_Exclude_My_Own_Prs()
    {
        // I authored PR 1 and am the teammate; raw union keeps my own PR.
        var reviewer = """{"value":[]}""";
        var active = $$"""{"value":[{{Pr(1, "" + Me, "2026-01-01T00:00:00Z")}}]}""";
        var handler = new DispatchHandler(reviewer, active);
        var adapter = Adapter(handler, Directory(
            new TeamMembership(TeamId, "Proj", new HashSet<string> { Me.ToString() })));

        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Single(prs);
        Assert.Equal(1, prs[0].PullRequestId);
    }

    [Fact]
    public async Task Sorts_By_CreationDate_Descending()
    {
        var reviewer = """{"value":[]}""";
        var active = $$"""
            {"value":[
              {{Pr(1, TeammateId, "2026-01-01T00:00:00Z")}},
              {{Pr(2, TeammateId, "2026-03-01T00:00:00Z")}},
              {{Pr(3, TeammateId, "2026-02-01T00:00:00Z")}}
            ]}
            """;
        var handler = new DispatchHandler(reviewer, active);
        var adapter = Adapter(handler, Directory(
            new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })));

        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 2, 3, 1 }, prs.Select(p => p.PullRequestId).ToArray());
    }

    [Fact]
    public async Task Team_Directory_Is_Resolved_Once_And_Cached_Across_Loads()
    {
        var calls = 0;
        Func<CancellationToken, Task<TeamDirectory>> resolve = _ =>
        {
            calls++;
            return Task.FromResult(new TeamDirectory(
                [new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })]));
        };
        var handler = new DispatchHandler("""{"value":[]}""", """{"value":[]}""");
        var adapter = Adapter(handler, resolve);

        await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);
        await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Project_Scope_Filters_Teams_To_Context_Project()
    {
        // Two teams: one in "Proj" (in scope), one in "Other" (out of scope). Under project
        // scope only the in-scope team's reviewer call fires and only its members count.
        var otherTeam = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
        var reviewer = """{"value":[]}""";
        var active = $$"""{"value":[{{Pr(9, "other-teammate", "2026-01-01T00:00:00Z")}}]}""";
        var handler = new DispatchHandler(reviewer, active);
        var adapter = Adapter(handler, Directory(
            new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId }),
            new TeamMembership(otherTeam, "Other", new HashSet<string> { "other-teammate" })),
            PrScope.Project);

        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        // Only the in-scope team's reviewer call; the out-of-scope teammate author is not matched.
        Assert.Equal(1, handler.ReviewerCalls);
        Assert.Empty(prs);
    }

    [Fact]
    public async Task Concurrent_Team_Loads_Share_One_Directory_Build()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<TeamDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<TeamDirectory>> resolve = _ =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        };
        var handler = new DispatchHandler("""{"value":[]}""", """{"value":[]}""");
        var adapter = Adapter(handler, resolve);

        // Two Team loads race before the directory build finishes: a plain `_teams ??= await …`
        // lets both see null and build it twice. Single-flight must collapse them onto one build.
        var first = adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);
        var second = adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);
        gate.SetResult(new TeamDirectory([new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })]));
        await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Team_Directory_Is_Rebuilt_After_A_Failed_Build()
    {
        var calls = 0;
        var fail = true;
        Func<CancellationToken, Task<TeamDirectory>> resolve = _ =>
        {
            Interlocked.Increment(ref calls);
            return fail
                ? Task.FromException<TeamDirectory>(new AdoApiException(HttpStatusCode.ServiceUnavailable, "flaky"))
                : Task.FromResult(new TeamDirectory([new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })]));
        };
        var handler = new DispatchHandler("""{"value":[]}""", """{"value":[]}""");
        var adapter = Adapter(handler, resolve);

        await Assert.ThrowsAsync<AdoApiException>(
            () => adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken));

        // A faulted build must be evicted, not cached: the next load retries and can succeed.
        fail = false;
        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Empty(prs);
    }

    [Fact]
    public async Task Team_Directory_Is_Rebuilt_After_A_Canceled_Build()
    {
        // An HttpClient timeout surfaces the shared build as a *canceled* task (a foreign token),
        // not a fault. If eviction only fired for faults, the canceled task would be cached forever
        // and every later Team load would instantly rethrow with no network.
        var calls = 0;
        var fail = true;
        using var foreign = new CancellationTokenSource();
        await foreign.CancelAsync();
        Func<CancellationToken, Task<TeamDirectory>> resolve = _ =>
        {
            Interlocked.Increment(ref calls);
            return fail
                ? Task.FromCanceled<TeamDirectory>(foreign.Token)
                : Task.FromResult(new TeamDirectory([new TeamMembership(TeamId, "Proj", new HashSet<string> { TeammateId })]));
        };
        var handler = new DispatchHandler("""{"value":[]}""", """{"value":[]}""");
        var adapter = Adapter(handler, resolve);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken));

        // The canceled build must be evicted so the next load retries and can succeed.
        fail = false;
        var prs = await adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Empty(prs);
    }

    [Fact]
    public async Task Teams_Resolution_Failure_Surfaces_As_Error()
    {
        Func<CancellationToken, Task<TeamDirectory>> resolve = _ =>
            Task.FromException<TeamDirectory>(new AdoApiException(HttpStatusCode.Forbidden, "no team access"));
        var handler = new DispatchHandler("""{"value":[]}""", """{"value":[]}""");
        var adapter = Adapter(handler, resolve);

        await Assert.ThrowsAsync<AdoApiException>(
            () => adapter.ListPullRequestsAsync(PrListFilter.Team, TestContext.Current.CancellationToken));
    }
}
