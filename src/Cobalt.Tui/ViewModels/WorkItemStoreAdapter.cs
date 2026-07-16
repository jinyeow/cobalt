using System.Collections.Concurrent;
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

    // Allowed states are per-project process metadata that never change within a session, but the
    // state-change dialog re-fetches them every time it opens. Cache per (project, type) so the
    // second and later opens are instant. A faulted fetch is evicted (below) so a transient
    // failure can be retried; the Core transport stays stateless.
    private readonly ConcurrentDictionary<(string Project, string Type), Lazy<Task<IReadOnlyList<WorkItemStateDto>>>>
        _statesCache = new();

    public Task<IReadOnlyList<WorkItem>> QueryMyWorkItemsAsync(WorkItemQuery query, CancellationToken ct) =>
        api.QueryMyWorkItemsAsync(query, Scope, ct);

    public Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct) =>
        api.GetWorkItemAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct) =>
        api.GetCommentsAsync(id, project, ct);

    public Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct)
    {
        var key = (project ?? "", type);
        // Lazy so the fetch is started at most once per key even under concurrent opens; the
        // detached start (CancellationToken.None) means a joiner is never bound to a cancelled
        // starter — each caller observes its own token via WaitAsync (ADR 0008).
        var lazy = _statesCache.GetOrAdd(key, _ =>
            new Lazy<Task<IReadOnlyList<WorkItemStateDto>>>(() => api.GetStatesAsync(type, project, CancellationToken.None)));
        return AwaitStatesAsync(key, lazy, ct);
    }

    private async Task<IReadOnlyList<WorkItemStateDto>> AwaitStatesAsync(
        (string, string) key,
        Lazy<Task<IReadOnlyList<WorkItemStateDto>>> lazy,
        CancellationToken ct)
    {
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Evict only if the shared fetch itself faulted (so a retry re-fetches). A caller
            // cancel where the fetch is still pending/succeeded must not drop a good entry; and
            // removing this exact Lazy never evicts a newer attempt.
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
            {
                _statesCache.TryRemove(new KeyValuePair<(string, string), Lazy<Task<IReadOnlyList<WorkItemStateDto>>>>(key, lazy));
            }

            throw;
        }
    }

    public Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct) =>
        api.UpdateFieldsAsync(id, patch, project, ct);

    public Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct) =>
        api.AddCommentAsync(id, text, project, ct);
}
