using Cobalt.Core.Ado;

namespace Cobalt.Core.Tests.Ado;

public class WiqlBuilderTests
{
    [Fact]
    public void Default_Excludes_The_Five_Completed_States_Including_Resolved()
    {
        var wiql = WiqlBuilder.MyItems(new WorkItemQuery());

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
            "AND [System.State] NOT IN ('Closed', 'Done', 'Completed', 'Resolved', 'Removed') " +
            "ORDER BY [System.ChangedDate] DESC",
            wiql);
        Assert.Contains("'Resolved'", wiql);
    }

    [Fact]
    public void IncludeCompleted_Drops_The_State_Clause()
    {
        var wiql = WiqlBuilder.MyItems(new WorkItemQuery(IncludeCompleted: true));

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
            "ORDER BY [System.ChangedDate] DESC",
            wiql);
        Assert.DoesNotContain("System.State", wiql);
    }

    [Fact]
    public void Project_Clause_Is_Present_And_Escaped()
    {
        var wiql = WiqlBuilder.MyItems(new WorkItemQuery(Project: "Fabrikam"));

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
            "AND [System.State] NOT IN ('Closed', 'Done', 'Completed', 'Resolved', 'Removed') " +
            "AND [System.TeamProject] = 'Fabrikam' " +
            "ORDER BY [System.ChangedDate] DESC",
            wiql);
    }

    [Fact]
    public void Project_With_Apostrophe_Doubles_The_Single_Quote()
    {
        var wiql = WiqlBuilder.MyItems(new WorkItemQuery(Project: "O'Brien"));

        Assert.Contains("[System.TeamProject] = 'O''Brien'", wiql);
    }

    [Fact]
    public void IncludeCompleted_With_Project_Keeps_Project_Clause_Only()
    {
        var wiql = WiqlBuilder.MyItems(new WorkItemQuery(IncludeCompleted: true, Project: "Fabrikam"));

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
            "AND [System.TeamProject] = 'Fabrikam' " +
            "ORDER BY [System.ChangedDate] DESC",
            wiql);
    }

    [Fact]
    public void Order_By_Is_Always_Present()
    {
        Assert.EndsWith("ORDER BY [System.ChangedDate] DESC", WiqlBuilder.MyItems(new WorkItemQuery()));
        Assert.EndsWith("ORDER BY [System.ChangedDate] DESC", WiqlBuilder.MyItems(new WorkItemQuery(true, "P")));
    }
}
