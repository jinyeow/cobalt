namespace Cobalt.Core.Config;

/// <summary>One org/project pair the user can point Cobalt at.</summary>
public sealed record AdoContext
{
    public required string Name { get; init; }
    public required Uri OrganizationUrl { get; init; }
    public required string Project { get; init; }

    /// <summary>
    /// How wide the PR lists reach. Defaults to <see cref="PrScope.Org"/> (whole
    /// organization) when <c>pr_scope</c> is absent from config.
    /// </summary>
    public PrScope PrScope { get; init; } = PrScope.Org;
}
