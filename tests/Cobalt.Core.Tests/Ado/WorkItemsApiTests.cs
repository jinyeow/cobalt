using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class WorkItemsApiTests : IDisposable
{
    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "My Project", // deliberately has a space
    };

    private readonly List<IDisposable> _disposables = [];

    private WorkItemsApi Api(FakeHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        return new WorkItemsApi(new AdoHttp(httpClient), Context);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task QueryMyWorkItems_Runs_Wiql_Then_Batches_Ids()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[{"id":42},{"id":7}]}""")
            .Respond(HttpStatusCode.OK,
                """
                {"value":[
                  {"id":42,"fields":{"System.Title":"Fix login","System.State":"Active","System.WorkItemType":"Bug",
                    "System.AssignedTo":{"displayName":"Jin","uniqueName":"jin@x"},"System.IterationPath":"Proj\\Sprint 1"}},
                  {"id":7,"fields":{"System.Title":"Add logs","System.State":"New","System.WorkItemType":"Task"}}
                ]}
                """);

        var items = await Api(handler).QueryMyWorkItemsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        Assert.Equal(42, items[0].Id);
        Assert.Equal("Fix login", items[0].Title);
        Assert.Equal("Active", items[0].State);
        Assert.Equal("Bug", items[0].WorkItemType);
        Assert.Equal("Jin", items[0].AssignedToDisplayName);

        // WIQL is POSTed to the project-scoped, URL-encoded endpoint
        var wiqlUri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("My%20Project", wiqlUri);
        Assert.Contains("wiql", wiqlUri);
        Assert.Contains("@Me", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task QueryMyWorkItems_Pages_Batch_Calls_Over_200_Ids()
    {
        // 250 ids from WIQL must become two workitemsbatch calls (200 + 50).
        var wiqlIds = string.Join(",", Enumerable.Range(1, 250).Select(i => $"{{\"id\":{i}}}"));
        var page1 = string.Join(",", Enumerable.Range(1, 200).Select(WorkItemJson));
        var page2 = string.Join(",", Enumerable.Range(201, 50).Select(WorkItemJson));
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, $"{{\"workItems\":[{wiqlIds}]}}")
            .Respond(HttpStatusCode.OK, $"{{\"value\":[{page1}]}}")
            .Respond(HttpStatusCode.OK, $"{{\"value\":[{page2}]}}");

        var items = await Api(handler).QueryMyWorkItemsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(250, items.Count);
        Assert.Equal(1, items[0].Id); // WIQL order preserved across pages
        Assert.Equal(250, items[^1].Id);
        Assert.Equal(3, handler.Requests.Count); // 1 WIQL + 2 batch pages
    }

    private static string WorkItemJson(int id) =>
        $"{{\"id\":{id},\"fields\":{{\"System.Title\":\"item {id}\",\"System.State\":\"New\",\"System.WorkItemType\":\"Task\"}}}}";

    [Fact]
    public async Task QueryMyWorkItems_Project_Scope_Uses_Project_Route_For_Wiql_And_Batch()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[{"id":42}]}""")
            .Respond(HttpStatusCode.OK,
                """{"value":[{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}]}""");

        await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(), PrScope.Project, TestContext.Current.CancellationToken);

        Assert.Contains("My%20Project/_apis/wit/wiql", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("My%20Project/_apis/wit/workitemsbatch", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task QueryMyWorkItems_Org_Scope_Uses_Org_Route_For_Wiql_And_Batch()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[{"id":42}]}""")
            .Respond(HttpStatusCode.OK,
                """{"value":[{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}]}""");

        await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(), PrScope.Org, TestContext.Current.CancellationToken);

        // Org scope drops the project segment entirely for both routes.
        var wiql = handler.Requests[0].RequestUri!.AbsoluteUri;
        var batch = handler.Requests[1].RequestUri!.AbsoluteUri;
        Assert.Contains("/_apis/wit/wiql", wiql);
        Assert.DoesNotContain("My%20Project", wiql);
        Assert.Contains("/_apis/wit/workitemsbatch", batch);
        Assert.DoesNotContain("My%20Project", batch);
    }

    [Fact]
    public async Task QueryMyWorkItems_Project_Filter_Forces_Org_Route_And_Wiql_Clause()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[{"id":42}]}""")
            .Respond(HttpStatusCode.OK,
                """{"value":[{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}]}""");

        // Project-scoped but filtering to a *different* project must use the org route so
        // the [System.TeamProject] clause can reach across projects.
        await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(Project: "Fabrikam"), PrScope.Project, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("My%20Project", handler.Requests[0].RequestUri!.AbsoluteUri);
        // The JSON encoder escapes the single quotes around the project name, so match the
        // stable identifiers instead of the quoted literal.
        Assert.Contains("System.TeamProject", handler.RequestBodies[0]);
        Assert.Contains("Fabrikam", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task QueryMyWorkItems_IncludeCompleted_Sends_Body_Without_State_Clause()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[]}""");

        await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(IncludeCompleted: true), PrScope.Org, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("System.State", handler.RequestBodies[0]);
        Assert.Contains("@Me", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task QueryMyWorkItems_ListFields_Request_Includes_TeamProject()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, """{"workItems":[{"id":42}]}""")
            .Respond(HttpStatusCode.OK,
                """{"value":[{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug","System.TeamProject":"Fabrikam"}}]}""");

        var items = await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(), PrScope.Org, TestContext.Current.CancellationToken);

        Assert.Contains("System.TeamProject", handler.RequestBodies[1]); // batch fields list
        Assert.Equal("Fabrikam", items[0].TeamProject);
    }

    [Fact]
    public async Task QueryMyWorkItems_Empty_Skips_Batch_Call()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"workItems":[]}""");

        var items = await Api(handler).QueryMyWorkItemsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(items);
        Assert.Single(handler.Requests); // no batch request made
    }

    [Fact]
    public async Task GetWorkItem_Parses_Fields_And_Description()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"id":42,"fields":{"System.Title":"Fix login","System.State":"Active","System.WorkItemType":"Bug",
              "System.Description":"<p>steps</p>","System.Tags":"ui; auth",
              "Microsoft.VSTS.Common.Priority":2}}
            """);

        var item = await Api(handler).GetWorkItemAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal("Fix login", item.Title);
        Assert.Equal("<p>steps</p>", item.DescriptionHtml);
        Assert.Equal(["ui", "auth"], item.Tags);
        Assert.Equal(2, item.Priority);
    }

    [Fact]
    public async Task UpdateFields_Sends_JsonPatch_To_Work_Item()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":42,"fields":{"System.Title":"Renamed","System.State":"Active","System.WorkItemType":"Bug"}}""");

        var patch = new JsonPatchBuilder().SetField("System.Title", "Renamed");
        var updated = await Api(handler).UpdateFieldsAsync(42, patch, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("application/json-patch+json", handler.ContentTypes[0]);
        Assert.Contains("/fields/System.Title", handler.RequestBodies[0]);
        Assert.Contains("42", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Renamed", updated.Title);
    }

    [Fact]
    public async Task GetStates_Returns_Allowed_States_For_Type()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"value":[{"name":"New","category":"Proposed","color":"b2b2b2"},{"name":"Active","category":"InProgress","color":"007acc"}]}""");

        var states = await Api(handler).GetStatesAsync("Bug", TestContext.Current.CancellationToken);

        Assert.Equal(["New", "Active"], states.Select(s => s.Name));
        Assert.Contains("workitemtypes/Bug/states", Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString()));
    }

    [Fact]
    public async Task GetComments_Parses_Thread()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"comments":[
              {"id":1,"text":"<p>first</p>","createdBy":{"displayName":"Jin"},"createdDate":"2026-01-01T10:00:00Z"},
              {"id":2,"text":"second","createdBy":{"displayName":"Sam"},"createdDate":"2026-01-02T10:00:00Z"}
            ]}
            """);

        var comments = await Api(handler).GetCommentsAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(2, comments.Count);
        Assert.Equal("Jin", comments[0].Author);
        Assert.Contains("first", comments[0].TextMarkdown);
    }

    [Fact]
    public async Task AddComment_Posts_Text()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":3,"text":"hi","createdBy":{"displayName":"Jin"},"createdDate":"2026-01-03T10:00:00Z"}""");

        await Api(handler).AddCommentAsync(42, "hi there", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("comments", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("hi there", handler.RequestBodies[0]);
    }
}
