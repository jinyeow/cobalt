using System.Net;
using System.Text;
using System.Text.Json;
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

        var item = await Api(handler).GetWorkItemAsync(42, cancellationToken: TestContext.Current.CancellationToken);

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
        var updated = await Api(handler).UpdateFieldsAsync(42, patch, cancellationToken: TestContext.Current.CancellationToken);

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

        var states = await Api(handler).GetStatesAsync("Bug", cancellationToken: TestContext.Current.CancellationToken);

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

        var comments = await Api(handler).GetCommentsAsync(42, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, comments.Count);
        Assert.Equal("Jin", comments[0].Author);
        Assert.Contains("first", comments[0].TextMarkdown);
    }

    [Fact]
    public async Task AddComment_Posts_Text()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":3,"text":"hi","createdBy":{"displayName":"Jin"},"createdDate":"2026-01-03T10:00:00Z"}""");

        await Api(handler).AddCommentAsync(42, "hi there", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("comments", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("hi there", handler.RequestBodies[0]);
    }

    // ---- H1: cross-project drill-in threads the item's own project through the route ----

    [Fact]
    public async Task GetWorkItem_Uses_The_Items_Own_Project_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}""");

        await Api(handler).GetWorkItemAsync(42, "Fabrikam", TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("Fabrikam/_apis/wit/workitems/42", uri);
        Assert.DoesNotContain("My%20Project", uri); // never the context project
    }

    [Fact]
    public async Task UpdateFields_Uses_The_Items_Own_Project_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}""");

        var patch = new JsonPatchBuilder().SetField("System.State", "Active");
        await Api(handler).UpdateFieldsAsync(42, patch, "Fabrikam", TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("Fabrikam/_apis/wit/workitems/42", uri);
        Assert.DoesNotContain("My%20Project", uri);
    }

    [Fact]
    public async Task GetStates_Uses_The_Items_Own_Project_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"value":[{"name":"New","category":"Proposed","color":"b2b2b2"}]}""");

        await Api(handler).GetStatesAsync("Bug", "Fabrikam", TestContext.Current.CancellationToken);

        // States are per-project process metadata: the item's project, not the context's.
        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("Fabrikam/_apis/wit/workitemtypes/Bug/states", uri);
        Assert.DoesNotContain("My%20Project", uri);
    }

    [Fact]
    public async Task GetComments_Uses_The_Items_Own_Project_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"comments":[]}""");

        await Api(handler).GetCommentsAsync(42, "Fabrikam", TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("Fabrikam/_apis/wit/workItems/42/comments", uri);
        Assert.DoesNotContain("My%20Project", uri);
    }

    [Fact]
    public async Task AddComment_Uses_The_Items_Own_Project_Route()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":3,"text":"hi","createdBy":{"displayName":"Jin"},"createdDate":"2026-01-03T10:00:00Z"}""");

        await Api(handler).AddCommentAsync(42, "hi", "Fabrikam", TestContext.Current.CancellationToken);

        var uri = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("Fabrikam/_apis/wit/workItems/42/comments", uri);
        Assert.DoesNotContain("My%20Project", uri);
    }

    [Fact]
    public async Task Drill_In_Defaults_To_The_Context_Project_When_No_Project_Given()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":42,"fields":{"System.Title":"t","System.State":"Active","System.WorkItemType":"Bug"}}""");

        await Api(handler).GetWorkItemAsync(42, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("My%20Project/_apis/wit/workitems/42", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    // ---- NET-6: WIQL is capped with $top so an unbounded assigned-items list can't blow up ----

    [Fact]
    public async Task QueryMyWorkItems_Caps_The_Wiql_With_Top()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"workItems":[]}""");

        await Api(handler).QueryMyWorkItemsAsync(
            new WorkItemQuery(), PrScope.Org, TestContext.Current.CancellationToken);

        Assert.Contains("$top=200", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    // ---- NET-5: batch pages are dispatched concurrently ----

    [Fact]
    public async Task QueryMyWorkItems_Dispatches_Batch_Pages_Concurrently()
    {
        // 250 ids -> two workitemsbatch pages. If the pages were awaited one at a time, the second
        // page would not be issued until the first (gated) call returned, so SecondBatchArrived
        // would never complete. Concurrent dispatch issues both before either responds.
        var wiqlIds = string.Join(",", Enumerable.Range(1, 250).Select(i => $"{{\"id\":{i}}}"));
        var handler = new GatedBatchHandler($"{{\"workItems\":[{wiqlIds}]}}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/contoso/") };
        _disposables.Add(httpClient);
        var api = new WorkItemsApi(new AdoHttp(httpClient), Context);

        var query = api.QueryMyWorkItemsAsync(TestContext.Current.CancellationToken);
        await handler.SecondBatchArrived.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        handler.Release();
        var items = await query;

        Assert.Equal(2, handler.BatchCount);
        Assert.Equal(250, items.Count);
        Assert.Equal(1, items[0].Id);     // WIQL order preserved across concurrent pages
        Assert.Equal(250, items[^1].Id);
    }

    /// <summary>Answers the WIQL immediately, then gates every workitemsbatch call so pages overlap.</summary>
    private sealed class GatedBatchHandler(string wiqlJson) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _batchCount;

        public int BatchCount => Volatile.Read(ref _batchCount);
        public Task SecondBatchArrived => _secondBatch.Task;
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("wiql", StringComparison.Ordinal))
            {
                return Ok(wiqlJson);
            }

            var body = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (Interlocked.Increment(ref _batchCount) == 2)
            {
                _secondBatch.TrySetResult();
            }

            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return Ok(EchoRequestedIds(body));
        }

        // Echo the ids this page asked for back as work items, so the merge is correct regardless
        // of which page's gated response completes first.
        private static string EchoRequestedIds(string requestBody)
        {
            using var doc = JsonDocument.Parse(requestBody);
            var items = doc.RootElement.GetProperty("ids").EnumerateArray()
                .Select(e => $"{{\"id\":{e.GetInt64()},\"fields\":{{\"System.Title\":\"t\",\"System.State\":\"New\",\"System.WorkItemType\":\"Task\"}}}}");
            return $"{{\"value\":[{string.Join(",", items)}]}}";
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
