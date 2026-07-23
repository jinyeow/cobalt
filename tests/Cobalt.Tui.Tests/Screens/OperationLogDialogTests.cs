using Cobalt.Core.Ado;
using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The `:log` overlay's pure formatting (unit E): the render lists each ADO operation's name,
/// masked route shape, duration, and outcome, and never leaks the raw query. The dialog wrapper
/// itself is a thin <see cref="TextDialog"/> reuse, verified by UAT.
/// </summary>
public class OperationLogDialogTests
{
    [Fact]
    public void Format_Renders_Name_Masked_Route_Duration_And_Outcome_Without_The_Query()
    {
        var repo = Guid.NewGuid().ToString();
        var op = AdoOperation.FromRoute(
            "GET",
            $"_apis/git/repositories/{repo}/pullRequests/123?api-version=7.2&token=secret",
            TimeSpan.FromMilliseconds(42),
            200,
            new DateTimeOffset(2026, 1, 1, 10, 30, 0, TimeSpan.Zero));

        var text = OperationLogDialog.Format([op]);

        Assert.Contains("GET", text);
        Assert.Contains("repositories/{id}/pullRequests/{id}", text); // ids masked
        Assert.Contains("api-version=7.2", text);
        Assert.Contains("42ms", text);
        Assert.Contains("200", text);
        Assert.DoesNotContain("secret", text); // query trimmed to api-version by construction
    }

    [Fact]
    public void Format_Empty_History_Explains_There_Is_Nothing_Yet()
    {
        Assert.Equal("no ADO requests yet", OperationLogDialog.Format([]));
    }
}
