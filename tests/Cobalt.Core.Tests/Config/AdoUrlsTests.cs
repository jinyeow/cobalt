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
    public void WorkItem_Url_Uses_The_Items_Own_Project_Not_The_Context()
    {
        // Org-scoped: a work item living in another project must build its URL with that project.
        var url = AdoUrls.WorkItem(Context, 42, "Other Team");

        Assert.Equal("https://dev.azure.com/contoso/Other%20Team/_workitems/edit/42", url);
    }

    [Fact]
    public void WorkItem_Url_Falls_Back_To_Context_Project_When_Blank()
    {
        Assert.Equal(
            "https://dev.azure.com/contoso/My%20Project/_workitems/edit/42",
            AdoUrls.WorkItem(Context, 42, null));
    }

    [Fact]
    public void PullRequest_Url_Includes_Repo()
    {
        var url = AdoUrls.PullRequest(Context, "My Project", "web", 10);

        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_git/web/pullrequest/10", url);
    }

    [Fact]
    public void Encodes_Repo_With_Spaces()
    {
        var url = AdoUrls.PullRequest(Context, "My Project", "My Repo", 10);

        Assert.Contains("_git/My%20Repo/pullrequest/10", url);
    }

    [Fact]
    public void PullRequest_Url_Uses_The_Prs_Own_Project_Not_The_Context()
    {
        // Org-scoped: a PR living in another project must build its URL with that project.
        var url = AdoUrls.PullRequest(Context, "Other Team", "web", 10);

        Assert.Equal("https://dev.azure.com/contoso/Other%20Team/_git/web/pullrequest/10", url);
    }

    [Fact]
    public void PullRequest_Url_Falls_Back_To_Context_Project_When_Blank()
    {
        var url = AdoUrls.PullRequest(Context, "", "web", 10);

        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_git/web/pullrequest/10", url);
    }
}
