using Cobalt.Core.Ado;

namespace Cobalt.Core.Tests.Ado;

public class AdoOperationTests
{
    [Fact]
    public void Masks_Numeric_Id_Segment()
    {
        var shape = RouteShape.Of("_apis/git/repositories/abc/pullRequests/1234/threads");

        Assert.Equal("_apis/git/repositories/abc/pullRequests/{id}/threads", shape);
    }

    [Fact]
    public void Masks_Guid_Segment()
    {
        var shape = RouteShape.Of("_apis/git/repositories/5e2c1a2b-0000-1111-2222-333344445555/pullRequests");

        Assert.Equal("_apis/git/repositories/{id}/pullRequests", shape);
    }

    [Fact]
    public void Trims_Query_To_ApiVersion_Only()
    {
        var shape = RouteShape.Of("_apis/wit/workitems/999?api-version=7.2-preview.1&fields=System.Title");

        Assert.Equal("_apis/wit/workitems/{id}?api-version=7.2-preview.1", shape);
    }

    [Fact]
    public void Never_Leaks_A_Token_Query_Parameter()
    {
        var shape = RouteShape.Of("_apis/wit/workitems/999?access_token=super-secret&api-version=7.2-preview.1");

        Assert.DoesNotContain("super-secret", shape);
        Assert.DoesNotContain("access_token", shape);
    }

    [Fact]
    public void Path_Without_Query_Is_Unchanged_Besides_Masking()
    {
        var shape = RouteShape.Of("_apis/connectionData");

        Assert.Equal("_apis/connectionData", shape);
    }
}
