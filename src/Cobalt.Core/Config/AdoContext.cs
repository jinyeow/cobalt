namespace Cobalt.Core.Config;

/// <summary>One org/project pair the user can point Cobalt at.</summary>
public sealed record AdoContext
{
    public required string Name { get; init; }
    public required Uri OrganizationUrl { get; init; }
    public required string Project { get; init; }
}
