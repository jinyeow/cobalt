using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Core.Ado;

/// <summary>Work-item reads and writes for one project (SPEC §2).</summary>
public sealed class WorkItemsApi(AdoHttp http, AdoContext context)
{
    private const string ApiVersion = "api-version=7.2-preview.3";

    // Upper bound on the assigned-items list (matches GitApi.ListTop); a heavier assignee is
    // silently truncated to the most-recently-changed 200.
    private const int WiqlTop = 200;

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

    /// <summary>
    /// The context project name a null/blank <c>project</c> argument resolves to (see
    /// <see cref="ProjectSeg"/>). Callers that key a cache by project use this to fold a null
    /// project onto the same key as the explicit context project.
    /// </summary>
    public string ContextProject => context.Project;

    private string Project => Uri.EscapeDataString(context.Project);

    /// <summary>Path segment for a work-item call: the item's own project, or the context's when blank.</summary>
    private string ProjectSeg(string? project) =>
        Uri.EscapeDataString(string.IsNullOrEmpty(project) ? context.Project : project);

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
            // $top caps the assigned-items list: without it a heavy assignee pulls an unbounded id
            // set (then that many batch reads). 200 matches GitApi.ListTop; excess is truncated.
            $"{prefix}_apis/wit/wiql?$top={WiqlTop}&api-version=7.2-preview.2",
            wiql,
            WorkItemJsonContext.Default.WiqlQuery,
            WorkItemJsonContext.Default.WiqlResult,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ids = result.WorkItems.Select(w => w.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // The WIQL above is capped at $top=WiqlTop (200), so ids.Count is always <= 200 in
        // production and Chunk yields a single page; workitemsbatch itself caps at 200 ids
        // per call. Chunk stays here defensively in case the $top cap is ever raised, but with
        // one page in practice there is nothing to gain from dispatching pages concurrently, so
        // fetch them sequentially.
        var byId = new Dictionary<long, WorkItem>(ids.Count);
        foreach (var page in Chunk(ids, 200))
        {
            var batch = await BatchAsync(page, ListFields, orgRoute, cancellationToken).ConfigureAwait(false);
            foreach (var dto in batch.Value)
            {
                byId[dto.Id] = WorkItem.From(dto);
            }
        }

        // WIQL returns ids ordered; the batch endpoint does not, so re-sort by WIQL order.
        var ordered = new List<WorkItem>(ids.Count);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var item))
            {
                ordered.Add(item);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Detail read for one item. Under org scope a list row can belong to any project,
    /// so the caller threads the item's own <c>System.TeamProject</c> here (defaulting to
    /// the context project) — otherwise a cross-project drill-in queries the wrong project.
    /// </summary>
    public async Task<WorkItem> GetWorkItemAsync(
        long id, string? project = null, CancellationToken cancellationToken = default)
    {
        var fields = string.Join(',', DetailFields);
        var dto = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/wit/workitems/{id}?fields={Uri.EscapeDataString(fields)}&{ApiVersion}",
            WorkItemJsonContext.Default.WorkItemDto,
            cancellationToken).ConfigureAwait(false);
        return WorkItem.From(dto);
    }

    public Task<WorkItem> UpdateFieldsAsync(
        long id, JsonPatchBuilder patch, string? project = null, CancellationToken cancellationToken = default) =>
        UpdateFieldsCoreAsync(id, patch, project, cancellationToken);

    private async Task<WorkItem> UpdateFieldsCoreAsync(
        long id, JsonPatchBuilder patch, string? project, CancellationToken cancellationToken)
    {
        var dto = await http.SendRawAsync(
            HttpMethod.Patch,
            $"{ProjectSeg(project)}/_apis/wit/workitems/{id}?{ApiVersion}",
            patch.ToJson(),
            WorkItemJsonContext.Default.WorkItemDto,
            contentType: "application/json-patch+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return WorkItem.From(dto);
    }

    /// <summary>
    /// Allowed states for a work-item type. States are per-project process metadata, so
    /// this MUST target the item's own project (not the context's) under org scope.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(
        string workItemType, string? project = null, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(workItemType)}/states?api-version=7.2-preview.1",
            WorkItemJsonContext.Default.WorkItemStatesResult,
            cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        long id, string? project = null, CancellationToken cancellationToken = default)
    {
        var result = await http.GetJsonAsync(
            $"{ProjectSeg(project)}/_apis/wit/workItems/{id}/comments?api-version=7.2-preview.4",
            WorkItemJsonContext.Default.WorkItemCommentsResult,
            cancellationToken).ConfigureAwait(false);
        return [.. result.Comments.Select(WorkItemComment.From)];
    }

    public async Task<WorkItemComment> AddCommentAsync(
        long id, string text, string? project = null, CancellationToken cancellationToken = default)
    {
        // ADO stores comment text as HTML (like descriptions); the reader converts
        // HTML->Markdown, so we must convert Markdown->HTML on the way in to round-trip.
        var dto = await http.SendJsonAsync(
            HttpMethod.Post,
            $"{ProjectSeg(project)}/_apis/wit/workItems/{id}/comments?api-version=7.2-preview.4",
            new AddCommentRequest { Text = Text.HtmlMarkdown.ToHtml(text) },
            WorkItemJsonContext.Default.AddCommentRequest,
            WorkItemJsonContext.Default.WorkItemCommentDto,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return WorkItemComment.From(dto);
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
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
