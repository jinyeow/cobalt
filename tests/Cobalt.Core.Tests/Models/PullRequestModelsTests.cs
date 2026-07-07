using Cobalt.Core.Models;

namespace Cobalt.Core.Tests.Models;

public class PullRequestModelsTests
{
    [Fact]
    public void From_Maps_CreatedBy_Id_To_AuthorId()
    {
        var dto = new PullRequestDto
        {
            PullRequestId = 1,
            Title = "x",
            CreatedBy = new IdentityRefDto { Id = "author-guid", DisplayName = "Jin" },
        };

        var pr = PullRequest.From(dto);

        Assert.Equal("author-guid", pr.AuthorId);
        Assert.Equal("Jin", pr.Author);
    }

    [Fact]
    public void From_AuthorId_Defaults_To_Empty_When_Absent()
    {
        var pr = PullRequest.From(new PullRequestDto { PullRequestId = 1, Title = "x" });

        Assert.Equal("", pr.AuthorId);
    }
}
