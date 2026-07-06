using System.Net;
using System.Text;
using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Core.Tests.Fakes;

namespace Cobalt.Core.Tests.Ado;

public class GitApiDiffTests : IDisposable
{
    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "Proj",
    };

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
    public async Task GetLatestIteration_Returns_Highest_Id_With_Commits()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"value":[
              {"id":1,"sourceRefCommit":{"commitId":"s1"},"targetRefCommit":{"commitId":"t1"},"commonRefCommit":{"commitId":"b1"}},
              {"id":2,"sourceRefCommit":{"commitId":"s2"},"targetRefCommit":{"commitId":"t2"},"commonRefCommit":{"commitId":"b2"}}
            ]}
            """);

        var it = await Api(handler).GetLatestIterationAsync("repo-1", 10, TestContext.Current.CancellationToken);

        Assert.Equal(2, it!.Id);
        Assert.Equal("s2", it.SourceCommitId);
        Assert.Equal("b2", it.BaseCommitId);
    }

    [Fact]
    public async Task GetLatestIteration_Null_When_No_Iterations()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, """{"value":[]}""");

        var it = await Api(handler).GetLatestIterationAsync("repo-1", 10, TestContext.Current.CancellationToken);

        Assert.Null(it);
    }

    [Fact]
    public async Task GetIterationChanges_Lists_Edited_Added_Deleted_Files()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"changeEntries":[
              {"changeType":"edit","item":{"path":"/src/a.cs"}},
              {"changeType":"add","item":{"path":"/src/b.cs"}},
              {"changeType":"delete","item":{"path":"/old.cs"}},
              {"changeType":"edit","item":{"path":"/folder","isFolder":true}}
            ]}
            """);

        var changes = await Api(handler).GetIterationChangesAsync(
            "repo-1", 10, iterationId: 2, TestContext.Current.CancellationToken);

        // folders are excluded
        Assert.Equal(3, changes.Count);
        Assert.Equal("/src/a.cs", changes[0].Path);
        Assert.Equal(FileChangeKind.Edit, changes[0].ChangeType);
        Assert.Equal(FileChangeKind.Add, changes[1].ChangeType);
        Assert.Equal(FileChangeKind.Delete, changes[2].ChangeType);
    }

    [Fact]
    public async Task GetIterationChanges_Maps_Rename_Source_Path()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"changeEntries":[
              {"changeType":"edit, rename","sourceServerItem":"/old/name.cs","item":{"path":"/new/name.cs"}}
            ]}
            """);

        var changes = await Api(handler).GetIterationChangesAsync(
            "repo-1", 10, iterationId: 2, TestContext.Current.CancellationToken);

        Assert.Single(changes);
        Assert.Equal("/new/name.cs", changes[0].Path);
        Assert.Equal(FileChangeKind.Rename, changes[0].ChangeType);
        Assert.Equal("/old/name.cs", changes[0].OriginalPath);
    }

    [Fact]
    public async Task GetIterationChanges_Falls_Back_To_OriginalPath()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"changeEntries":[
              {"changeType":"rename","originalPath":"/from.cs","item":{"path":"/to.cs"}}
            ]}
            """);

        var changes = await Api(handler).GetIterationChangesAsync(
            "repo-1", 10, iterationId: 2, TestContext.Current.CancellationToken);

        Assert.Single(changes);
        Assert.Equal("/to.cs", changes[0].Path);
        Assert.Equal("/from.cs", changes[0].OriginalPath);
    }

    [Fact]
    public async Task GetIterationChanges_Non_Rename_Has_Null_OriginalPath()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """
            {"changeEntries":[
              {"changeType":"edit","item":{"path":"/src/a.cs"}}
            ]}
            """);

        var changes = await Api(handler).GetIterationChangesAsync(
            "repo-1", 10, iterationId: 2, TestContext.Current.CancellationToken);

        Assert.Single(changes);
        Assert.Null(changes[0].OriginalPath);
    }

    [Fact]
    public async Task GetFileContent_Requests_Path_At_Commit_And_Returns_Text()
    {
        var handler = new FakeHttpHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("line1\nline2\n", Encoding.UTF8, "text/plain"),
        });

        var content = await Api(handler).GetFileContentAsync(
            "repo-1", "/src/a.cs", "commit-abc", TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline2\n", content);
        var uri = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("repositories/repo-1/items", uri);
        Assert.Contains("path=/src/a.cs", uri);
        Assert.Contains("versionDescriptor.version=commit-abc", uri);
        Assert.Contains("versionDescriptor.versionType=commit", uri);
    }

    [Fact]
    public async Task GetFileContent_Returns_Empty_On_404_Missing_Side()
    {
        // A newly-added file has no base version → 404; treated as empty (whole file added).
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotFound,
            """{"message":"TF401174: path not found"}""");

        var content = await Api(handler).GetFileContentAsync(
            "repo-1", "/new.cs", "base-commit", TestContext.Current.CancellationToken);

        Assert.Equal("", content);
    }

    [Fact]
    public async Task AddLineComment_Posts_Thread_With_Right_Anchor()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":5,"status":"active","comments":[{"id":1,"content":"nit","author":{"displayName":"Jin"},"commentType":"text"}]}""");

        await Api(handler).AddLineCommentAsync(
            "repo-1", 10, "/src/a.cs", line: 42, rightSide: true, "nit: rename", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("threads", handler.Requests[0].RequestUri!.AbsoluteUri);
        var body = handler.RequestBodies[0]!;
        Assert.Contains("\"filePath\":\"/src/a.cs\"", body);
        Assert.Contains("rightFileStart", body);
        Assert.Contains("\"line\":42", body);
        Assert.Contains("nit: rename", body);
    }

    [Fact]
    public async Task AddLineComment_Left_Side_Uses_LeftFile_Anchor()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK,
            """{"id":5,"status":"active","comments":[{"id":1,"content":"x","author":{"displayName":"Jin"},"commentType":"text"}]}""");

        await Api(handler).AddLineCommentAsync(
            "repo-1", 10, "/src/a.cs", line: 7, rightSide: false, "on deleted line", TestContext.Current.CancellationToken);

        var body = handler.RequestBodies[0]!;
        Assert.Contains("leftFileStart", body);
        Assert.DoesNotContain("rightFileStart", body);
    }
}
