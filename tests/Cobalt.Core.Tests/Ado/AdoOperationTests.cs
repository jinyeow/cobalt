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

    [Fact]
    public void Absolute_Url_With_Credentials_Never_Leaks_Host_Userinfo_Or_Query()
    {
        var shape = RouteShape.Of("https://user:PAT@dev.azure.com/contoso/_apis/x?sig=SECRET");

        Assert.DoesNotContain("PAT", shape);
        Assert.DoesNotContain("SECRET", shape);
        Assert.DoesNotContain("@", shape);
        Assert.DoesNotContain("dev.azure.com", shape);
    }

    [Fact]
    public void Malformed_ApiVersion_With_Smuggled_Segment_Drops_The_Whole_Query()
    {
        var shape = RouteShape.Of("_apis/wit/workitems/999?api-version=7.1;sig=SECRET");

        Assert.Equal("_apis/wit/workitems/{id}", shape);
        Assert.DoesNotContain("SECRET", shape);
    }

    [Theory]
    [InlineData("7.2")]
    [InlineData("7.2-preview")]
    [InlineData("7.2-preview.1")]
    public void WellFormed_ApiVersion_Shapes_Survive(string apiVersion)
    {
        var shape = RouteShape.Of($"_apis/connectionData?api-version={apiVersion}");

        Assert.Equal($"_apis/connectionData?api-version={apiVersion}", shape);
    }

    [Fact]
    public void FromRoute_Is_The_Only_Construction_Path_And_Always_Redacts()
    {
        // AdoOperation has no public constructor that accepts a raw route string — FromRoute
        // is the sole entry point, and it always pipes the route through RouteShape.Of, so a
        // caller cannot smuggle an unredacted secret into a stored operation.
        var op = AdoOperation.FromRoute(
            "PATCH", "https://user:PAT@dev.azure.com/contoso/_apis/x?sig=SECRET",
            TimeSpan.FromMilliseconds(5), 200, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("PAT", op.RouteShape);
        Assert.DoesNotContain("SECRET", op.RouteShape);
        Assert.DoesNotContain("@", op.RouteShape);
    }
}
