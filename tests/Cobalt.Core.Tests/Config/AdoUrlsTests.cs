using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class AdoUrlsTests
{
    private static readonly AdoContext Context = new()
    {
        Name = "work",
        OrganizationUrl = new Uri("https://dev.azure.com/contoso"),
        Project = "My Project",
    };

    [Fact]
    public void WorkItem_Url_Is_Web_Editable_Link()
    {
        var url = AdoUrls.WorkItem(Context, 42);

        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_workitems/edit/42", url);
    }

    [Fact]
    public void PullRequest_Url_Includes_Repo()
    {
        var url = AdoUrls.PullRequest(Context, "web", 10);

        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_git/web/pullrequest/10", url);
    }

    [Fact]
    public void Encodes_Repo_With_Spaces()
    {
        var url = AdoUrls.PullRequest(Context, "My Repo", 10);

        Assert.Contains("_git/My%20Repo/pullrequest/10", url);
    }
}
