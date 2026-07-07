namespace Cobalt.Core.Config;

/// <summary>
/// How wide the pull-request lists reach: a single <see cref="AdoContext.Project"/>,
/// or every project in the organization. The product default is <see cref="Org"/>.
/// </summary>
public enum PrScope
{
    /// <summary>Only the context's configured project (the classic single-project view).</summary>
    Project,

    /// <summary>Every project in the organization (the default).</summary>
    Org,
}
