using Cobalt.Core.Ado;
using Cobalt.Core.Models;

namespace Cobalt.Tui.ViewModels;

/// <summary>Adapts the transport-level <see cref="WorkItemsApi"/> to the view-model interfaces.</summary>
public sealed class WorkItemStoreAdapter(WorkItemsApi api) : IWorkItemSource, IWorkItemStore
{
    public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(CancellationToken ct) =>
        api.QueryMyWorkItemsAsync(ct);

    public Task<WorkItem> GetWorkItemAsync(long id, CancellationToken ct) =>
        api.GetWorkItemAsync(id, ct);

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, CancellationToken ct) =>
        api.GetCommentsAsync(id, ct);

    public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, CancellationToken ct) =>
        api.GetStatesAsync(type, ct);

    public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, CancellationToken ct) =>
        api.UpdateFieldsAsync(id, patch, ct);

    public Task<WorkItemComment> AddCommentAsync(long id, string text, CancellationToken ct) =>
        api.AddCommentAsync(id, text, ct);
}
