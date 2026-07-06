using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>Work-item reads and writes for one project (SPEC §2).</summary>
public sealed class WorkItemsApi(AdoHttp http, AdoContext context)
{
    private const string ApiVersion = "api-version=7.2-preview.3";

    private static readonly string[] ListFields =
    [
        "System.Id", "System.WorkItemType", "System.Title", "System.State",
        "System.AssignedTo", "System.IterationPath", "System.Tags", "System.ChangedDate",
        "System.TeamProject",
    ];

    private static readonly string[] DetailFields =
    [
        .. ListFields,
        "System.AreaPath", "System.Description",
        "Microsoft.VSTS.Common.Priority", "Microsoft.VSTS.Scheduling.StoryPoints",
    ];

    private string Project => Uri.EscapeDataString(context.Project);

    /// <summary>Back-compat overload: project-scoped, hides completed items (the pre-filter default).</summary>
    public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(CancellationToken cancellationToken = default) =>
        QueryMyWorkItemsAsync(new WorkItemQuery(), PrScope.Project, cancellationToken);

    /// <summary>
    /// Items assigned to the caller, most-recently-changed first, shaped by <paramref name="query"/>.
    /// <paramref name="scope"/> selects org-wide (all projects) vs the context project. A
    /// non-null <see cref="WorkItemQuery.Project"/> also forces the org route so its
    /// <c>[System.TeamProject]</c> clause can reach any project, not just the context's.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(
        WorkItemQuery query, PrScope scope, CancellationToken cancellationToken = default)
    {
        // Org scope, or any explicit project narrowing, drops the project segment so the
        // WIQL/batch run org-wide; the [System.TeamProject] clause does the narrowing.
        var orgRoute = scope == PrScope.Org || !string.IsNullOrEmpty(query.Project);
        var prefix = orgRoute ? "" : $"{Project}/";
        var wiql = new WiqlQuery { Query = WiqlBuilder.MyItems(query) };

        var result = await http.SendJsonAsync(
            HttpMethod.Post,
            $"{prefix}_apis/wit/wiql?api-version=7.2-preview.2",
            wiql,
            WorkItemJsonContext.Default.WiqlQuery,
            WorkItemJsonContext.Default.WiqlResult,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ids = result.WorkItems.Select(w => w.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // workitemsbatch caps at 200 ids per call; page through and merge.
        var byId = new Dictionary<long, WorkItem>();
        foreach (var page in Chunk(ids, 200))
        {
            var batch = await BatchAsync(page, ListFields, orgRoute, cancellationToken).ConfigureAwait(false);
            foreach (var dto in batch.Value)
            {
                byId[dto.Id] = WorkItem.From(dto);
            }
        }

        // WIQL returns ids ordered; the batch endpoint does not, so re-sort by WIQL order.
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    public async Task<WorkItem> GetWorkItemAsync(long id, CancellationToken cancellationToken = default)
    {
        var fields = string.Join(',', DetailFields);
        var dto = await http.GetJsonAsync(
            $"{Project}/_apis/wit/workitems/{id}?fields={Uri.EscapeDataString(fields)}&{ApiVersion}",
            WorkItemJsonContext.Default.WorkItemDto,
            cancellationToken).ConfigureAwait(false);
        return WorkItem.From(dto);
    }

    public Task<WorkItem> UpdateFieldsAsync(
        long id, JsonPatchBuilder patch, CancellationToken cancellationToken = default) =>
        UpdateFieldsCoreAsync(id, patch, cancellationToken);

    private async Task<WorkItem> UpdateFieldsCoreAsync(
        long id, JsonPatchBuilder patch, CancellationToken cancellationToken)
    {
        var dto = await http.SendRawAsync(
            HttpMethod.Patch,
            $"{Project}/_apis/wit/workitems/{id}?{ApiVersion}",
            patch.ToJson(),
            WorkItemJsonContext.Default.WorkItemDto,
            contentType: "application/json-patch+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return WorkItem.From(dto);
    }

    public async Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(
        string workItemType, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{Project}/_apis/wit/workitemtypes/{Uri.EscapeDataString(workItemType)}/states?api-version=7.2-preview.1",
            WorkItemJsonContext.Default.WorkItemStatesResult,
            cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        long id, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{Project}/_apis/wit/workItems/{id}/comments?api-version=7.2-preview.4",
            WorkItemJsonContext.Default.WorkItemCommentsResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Comments.Select(WorkItemComment.From)];
    }

    public async Task<WorkItemComment> AddCommentAsync(
        long id, string text, CancellationToken cancellationToken = default)
    {
        // ADO stores comment text as HTML (like descriptions); the reader converts
        // HTML->Markdown, so we must convert Markdown->HTML on the way in to round-trip.
        var dto = await http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/wit/workItems/{id}/comments?api-version=7.2-preview.4",
            new AddCommentRequest { Text = Text.HtmlMarkdown.ToHtml(text) },
            WorkItemJsonContext.Default.AddCommentRequest,
            WorkItemJsonContext.Default.WorkItemCommentDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return WorkItemComment.From(dto);
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return [.. items.Skip(i).Take(size)];
        }
    }

    private Task<WorkItemBatchResult> BatchAsync(
        IReadOnlyList<long> ids, string[] fields, bool orgRoute, CancellationToken cancellationToken) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{(orgRoute ? "" : $"{Project}/")}_apis/wit/workitemsbatch?api-version=7.2-preview.1",
            new WorkItemBatchRequest { Ids = ids, Fields = fields },
            WorkItemJsonContext.Default.WorkItemBatchRequest,
            WorkItemJsonContext.Default.WorkItemBatchResult,
            cancellationToken: cancellationToken);
}
