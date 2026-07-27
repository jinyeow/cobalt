using Cobalt.Core.Ado;
using Cobalt.Core.Config;
using Cobalt.Core.Models;
using Cobalt.Tui.Tasks;

namespace Cobalt.Tui.ViewModels;

/// <summary>Adapts the transport-level <see cref="WorkItemsApi"/> to the view-model interfaces.</summary>
public sealed class WorkItemStoreAdapter(WorkItemsApi api, PrScope initialScope = PrScope.Org)
    : IWorkItemSource, IWorkItemStore
{
    /// <summary>The active list breadth (org = all projects, project = the context project); flipped by <c>:scope</c>.</summary>
    public PrScope Scope { get; set; } = initialScope;

    // Allowed states are per-project process metadata that never change within a session, but the
    // state-change dialog re-fetches them every time it opens. Cache per (project, type) so the
    // second and later opens are instant. JoinFlightCache owns the join contract: overlapping opens
    // share one fetch, a caller's cancel never cancels it for the others, and an unsuccessful fetch
    // is evicted so a transient failure can be retried. The Core transport stays stateless.
    private readonly JoinFlightCache<(string Project, string Type), IReadOnlyList<WorkItemStateDto>>
        _statesCache = new();

    public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
        api.QueryMyWorkItemsAsync(query, Scope, ct);

    public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) =>
        api.GetWorkItemAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) =>
        api.GetCommentsAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct)
    {
        // A null/blank project resolves to the context project on the wire, so fold it onto the
        // same cache key — otherwise a null-project and a context-project call would duplicate.
        var key = (string.IsNullOrEmpty(project) ? api.ContextProject : project, type);
        // The fetch keeps the caller's raw project (null resolves to the context project on the
        // wire); the folding above is cache-key-only. Started detached, so a joiner is never bound
        // to a cancelled starter — each caller observes its own token instead (ADR 0008).
        return _statesCache.GetOrJoinAsync(
            key, _ => api.GetStatesAsync(type, project, CancellationToken.None), ct);
    }

    public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) =>
        api.UpdateFieldsAsync(id, patch, project, ct);

    public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) =>
        api.AddCommentAsync(id, text, project, ct);
}
