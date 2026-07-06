namespace Cobalt.Core.Ado;

/// <summary>
/// The tunable parts of the "my work items" WIQL: whether completed states are
/// included (default hides them) and an optional single-project narrowing.
/// </summary>
public sealed record WorkItemQuery(bool IncludeCompleted = false, string? Project = null);
