using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class TeamsApiTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly List<IDisposable> _disposables = [];

    private TeamsApi Api(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new TeamsApi(new AdoHttp(httpClient));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task GetMyTeams_Hits_Org_Level_Mine_Route_And_Maps()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            $$"""
            {"value":[
              {"id":"{{TeamId}}","name":"Cobalt Team","projectId":"{{ProjectId}}","projectName":"Proj"}
            ]}
            """);

        var teams = await Api(handler).GetMyTeamsAsync(TestContext.Current.CancellationToken);

        var team = Assert.Single(teams);
        Assert.Equal(TeamId, team.Id);
        Assert.Equal("Cobalt Team", team.Name);
        Assert.Equal(ProjectId, team.ProjectId);
        Assert.Equal("Proj", team.ProjectName);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("_apis/teams", uri);
        Assert.Contains("$mine=true", Uri.UnescapeDataString(uri));
        Assert.Contains("api-version=", uri);
    }

    [Fact]
    public async Task GetTeamMembers_Hits_Project_Team_Members_Route_And_Maps()
    {
        var memberId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            $$"""
            {"value":[
              {"identity":{"id":"{{memberId}}","displayName":"Jin Yeow","uniqueName":"jin@contoso.com"} }
            ]}
            """);

        var members = await Api(handler).GetTeamMembersAsync(ProjectId, TeamId, TestContext.Current.CancellationToken);

        var member = Assert.Single(members);
        Assert.Equal(memberId, member.Id);
        Assert.Equal("Jin Yeow", member.DisplayName);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains($"_apis/projects/{ProjectId}/teams/{TeamId}/members", uri);
        Assert.Contains("api-version=", uri);
    }

    [Fact]
    public async Task GetMyTeams_Error_Envelope_Throws_AdoApiException()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Forbidden,
            """{"message":"no access"}""");

        var ex = await Assert.ThrowsAsync<AdoApiException>(
            () => Api(handler).GetMyTeamsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }
}
