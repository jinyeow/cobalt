using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>Adapts the transport-level <see cref="WorkItemsApi"/> to the view-model interfaces.</summary>
public sealed class WorkItemStoreAdapter(WorkItemsApi api, PrScope initialScope = PrScope.Org)
    : IWorkItemSource, IWorkItemStore
{
    /// <summary>The active list breadth (org = all projects, project = the context project); flipped by <c>:scope</c>.</summary>
    public PrScope Scope { get; set; } = initialScope;

    public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
        api.QueryMyWorkItemsAsync(query, Scope, ct);

    public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) =>
        api.GetWorkItemAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) =>
        api.GetCommentsAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct) =>
        api.GetStatesAsync(type, project, ct);

    public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) =>
        api.UpdateFieldsAsync(id, patch, project, ct);

    public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) =>
        api.AddCommentAsync(id, text, project, ct);
}
