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
    ];

    private static readonly string[] DetailFields =
    [
        .. ListFields,
        "System.AreaPath", "System.Description",
        "Microsoft.VSTS.Common.Priority", "Microsoft.VSTS.Scheduling.StoryPoints",
    ];

    private string Project => Uri.EscapeDataString(context.Project);

    /// <summary>Items assigned to the caller, excluding closed/removed, most-recently-changed first.</summary>
    public async Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(CancellationToken cancellationToken = default)
    {
        var wiql = new WiqlQuery
        {
            Query =
                "SELECT [System.Id] FROM WorkItems " +
                "WHERE [System.AssignedTo] = @Me " +
                "AND [System.State] NOT IN ('Closed', 'Done', 'Removed', 'Completed') " +
                "ORDER BY [System.ChangedDate] DESC",
        };

        var result = await http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/wit/wiql?api-version=7.2-preview.2",
            wiql,
            WorkItemJsonContext.Default.WiqlQuery,
            WorkItemJsonContext.Default.WiqlResult,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ids = result.WorkItems.Select(w => w.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // WIQL returns ids ordered; the batch endpoint does not preserve order, so re-sort.
        var batch = await BatchAsync(ids, ListFields, cancellationToken).ConfigureAwait(false);
        var byId = batch.Value.ToDictionary(w => w.Id, WorkItem.From);
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
        var dto = await http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/wit/workItems/{id}/comments?api-version=7.2-preview.4",
            new AddCommentRequest { Text = text },
            WorkItemJsonContext.Default.AddCommentRequest,
            WorkItemJsonContext.Default.WorkItemCommentDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return WorkItemComment.From(dto);
    }

    private Task<WorkItemBatchResult> BatchAsync(
        IReadOnlyList<long> ids, string[] fields, CancellationToken cancellationToken) =>
        http.SendJsonAsync(
            HttpMethod.Post,
            $"{Project}/_apis/wit/workitemsbatch?api-version=7.2-preview.1",
            new WorkItemBatchRequest { Ids = ids, Fields = fields },
            WorkItemJsonContext.Default.WorkItemBatchRequest,
            WorkItemJsonContext.Default.WorkItemBatchResult,
            cancellationToken: cancellationToken);
}
