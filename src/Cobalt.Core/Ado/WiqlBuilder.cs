namespace Cobalt.Core.Ado;

/// <summary>Builds the WIQL for the "my work items" query from a <see cref="WorkItemQuery"/>.</summary>
public static class WiqlBuilder
{
    // The states treated as "completed" and hidden by default. Today's list omitted
    // Resolved; it is included here so a resolved-then-idle item drops off too.
    private const string HideCompletedClause =
        " AND [System.State] NOT IN ('Closed', 'Done', 'Completed', 'Resolved', 'Removed')";

    public static string MyItems(WorkItemQuery query)
    {
        var state = query.IncludeCompleted ? "" : HideCompletedClause;
        var project = string.IsNullOrEmpty(query.Project)
            ? ""
            : $" AND [System.TeamProject] = '{query.Project.Replace("'", "''", StringComparison.Ordinal)}'";
        return "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me"
            + state + project + " ORDER BY [System.ChangedDate] DESC";
    }
}
