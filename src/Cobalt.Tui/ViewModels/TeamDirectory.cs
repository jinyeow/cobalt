namespace Cobalt.Tui.ViewModels;

/// <summary>
/// One team the signed-in user belongs to, with its resolved member identity ids.
/// <paramref name="ProjectName"/> lets the adapter narrow the fan-out under project scope.
/// </summary>
public sealed record TeamMembership(Guid TeamId, string ProjectName, IReadOnlySet<string> MemberIds);

/// <summary>
/// The user's team memberships, resolved once (one <c>$mine=true</c> call plus one members
/// call per team) and cached for the adapter's lifetime — memberships change rarely.
/// </summary>
public sealed record TeamDirectory(IReadOnlyList<TeamMembership> Teams);
