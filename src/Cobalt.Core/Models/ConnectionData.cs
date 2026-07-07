namespace Cobalt.Core.Models;

public sealed record ConnectionData
{
    public ConnectionAuthenticatedUser? AuthenticatedUser { get; init; }
}

public sealed record ConnectionAuthenticatedUser
{
    public Guid Id { get; init; }
    public string? ProviderDisplayName { get; init; }
    public string? CustomDisplayName { get; init; }
    public string? Descriptor { get; init; }
}

/// <summary>The signed-in user as Cobalt cares about them.</summary>
public sealed record AdoUser(Guid Id, string DisplayName, string? Descriptor);
