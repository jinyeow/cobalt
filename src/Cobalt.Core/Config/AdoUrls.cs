namespace Cobalt.Core.Config;

/// <summary>Builds Azure DevOps web URLs for a context (for `gx` open-in-browser and `yy` yank).</summary>
public static class AdoUrls
{
    /// <summary>
    /// The web URL for a work item. <paramref name="project"/> is the item's own project
    /// (which, under org scope, may differ from the context's); falls back to the context
    /// project when blank.
    /// </summary>
    public static string WorkItem(AdoContext context, long id, string? project = null) =>
        $"{Org(context)}/{Enc(string.IsNullOrEmpty(project) ? context.Project : project)}/_workitems/edit/{id}";

    /// <summary>
    /// The web URL for a PR. <paramref name="project"/> is the PR's own project (which,
    /// under org scope, may differ from the context's); falls back to the context project
    /// when blank.
    /// </summary>
    public static string PullRequest(AdoContext context, string project, string repository, int id) =>
        $"{Org(context)}/{Enc(string.IsNullOrEmpty(project) ? context.Project : project)}/_git/{Enc(repository)}/pullrequest/{id}";

    /// <summary>
    /// The web URL for a branch. <paramref name="project"/> is the branch's own project
    /// (which, under org scope, may differ from the context's); falls back to the context
    /// project when blank.
    /// </summary>
    public static string Branch(AdoContext context, string project, string repository, string branch) =>
        $"{Org(context)}/{Enc(string.IsNullOrEmpty(project) ? context.Project : project)}/_git/{Enc(repository)}?version=GB{Enc(branch)}";

    private static string Org(AdoContext context) => context.OrganizationUrl.AbsoluteUri.TrimEnd('/');

    private static string Enc(string segment) => Uri.EscapeDataString(segment);
}
