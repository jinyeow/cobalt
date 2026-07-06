namespace Cobalt.Core.Config;

/// <summary>Builds Azure DevOps web URLs for a context (for `gx` open-in-browser and `yy` yank).</summary>
public static class AdoUrls
{
    public static string WorkItem(AdoContext context, long id) =>
        $"{Org(context)}/{Enc(context.Project)}/_workitems/edit/{id}";

    /// <summary>
    /// The web URL for a PR. <paramref name="project"/> is the PR's own project (which,
    /// under org scope, may differ from the context's); falls back to the context project
    /// when blank.
    /// </summary>
    public static string PullRequest(AdoContext context, string project, string repository, int id) =>
        $"{Org(context)}/{Enc(string.IsNullOrEmpty(project) ? context.Project : project)}/_git/{Enc(repository)}/pullrequest/{id}";

    private static string Org(AdoContext context) => context.OrganizationUrl.AbsoluteUri.TrimEnd('/');

    private static string Enc(string segment) => Uri.EscapeDataString(segment);
}
