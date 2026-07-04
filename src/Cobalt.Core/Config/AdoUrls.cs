namespace Cobalt.Core.Config;

/// <summary>Builds Azure DevOps web URLs for a context (for `gx` open-in-browser and `yy` yank).</summary>
public static class AdoUrls
{
    public static string WorkItem(AdoContext context, long id) =>
        $"{Org(context)}/{Enc(context.Project)}/_workitems/edit/{id}";

    public static string PullRequest(AdoContext context, string repository, int id) =>
        $"{Org(context)}/{Enc(context.Project)}/_git/{Enc(repository)}/pullrequest/{id}";

    private static string Org(AdoContext context) => context.OrganizationUrl.AbsoluteUri.TrimEnd('/');

    private static string Enc(string segment) => Uri.EscapeDataString(segment);
}
