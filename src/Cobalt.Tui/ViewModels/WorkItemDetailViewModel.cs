using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Text;

namespace Cobalt.Tui.ViewModels;

/// <summary>
/// Work-item reads and writes the detail screen needs; abstracted for testing.
/// Every method threads the item's own <c>project</c> so a cross-project drill-in under
/// org scope targets the right project (H1), mirroring the PR store.
/// </summary>
public interface IWorkItemStore
{
    Task<WorkItem> GetWorkItemAsync(long id, string? project, CancellationToken ct);
    Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, string? project, CancellationToken ct);
    Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, string? project, CancellationToken ct);
    Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, string? project, CancellationToken ct);
    Task<WorkItemComment> AddCommentAsync(long id, string text, string? project, CancellationToken ct);
}

/// <summary>
/// Work-item detail: fields, HTML-as-Markdown description with a lossiness flag,
/// the comment thread, and the mutations (state, comment, description, fields).
/// </summary>
public sealed class WorkItemDetailViewModel(IWorkItemStore store, long id, string? project = null)
{
    // The item's own project (from the selected list row under org scope). Refined to the
    // loaded item's System.TeamProject once known, and threaded through every mutation so a
    // cross-project item's state/comment/patch calls hit its project, not the context's (H1).
    private string? _project = string.IsNullOrEmpty(project) ? null : project;

    public long Id => id;
    public bool IsLoading { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }

    public WorkItem? Item { get; private set; }
    public string DescriptionMarkdown { get; private set; } = "";
    public bool DescriptionLossy { get; private set; }
    public IReadOnlyList<WorkItemComment> Comments { get; private set; } = [];
    public IReadOnlyList<WorkItemStateDto> AvailableStates { get; private set; } = [];

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            Item = await store.GetWorkItemAsync(id, _project, ct).ConfigureAwait(false);
            // The loaded item is authoritative for its project; use it for the remaining
            // per-project calls (states especially are per-project process metadata).
            if (!string.IsNullOrEmpty(Item.TeamProject))
            {
                _project = Item.TeamProject;
            }

            var analysis = HtmlMarkdown.Analyze(Item.DescriptionHtml);
            DescriptionMarkdown = analysis.Markdown;
            DescriptionLossy = analysis.Lossy;

            // Comments and the available-states list are independent per-project reads: run them
            // together so the detail pane opens a round-trip sooner instead of back-to-back. WhenAll
            // surfaces the first fault and observes both, so one failing cannot orphan the other onto
            // the crash-log hook (ADR 0013).
            var commentsTask = store.GetCommentsAsync(id, _project, ct);
            var statesTask = store.GetStatesAsync(Item.WorkItemType, _project, ct);
            await Task.WhenAll(commentsTask, statesTask).ConfigureAwait(false);
            Comments = await commentsTask.ConfigureAwait(false);
            AvailableStates = await statesTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public Task ChangeStateAsync(string state, CancellationToken ct) =>
        MutateAsync(new JsonPatchBuilder().SetField("System.State", state), ct);

    public Task AssignAsync(string uniqueName, CancellationToken ct) =>
        MutateAsync(new JsonPatchBuilder().SetField("System.AssignedTo", uniqueName), ct);

    public Task SetTitleAsync(string title, CancellationToken ct) =>
        MutateAsync(new JsonPatchBuilder().SetField("System.Title", title), ct);

    public Task SetTagsAsync(IEnumerable<string> tags, CancellationToken ct) =>
        MutateAsync(new JsonPatchBuilder().SetField("System.Tags", string.Join("; ", tags)), ct);

    public Task SaveDescriptionAsync(string markdown, CancellationToken ct) =>
        MutateAsync(new JsonPatchBuilder().SetField("System.Description", HtmlMarkdown.ToHtml(markdown)), ct);

    public async Task AddCommentAsync(string text, CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            await store.AddCommentAsync(id, text, _project, ct).ConfigureAwait(false);
            Comments = await store.GetCommentsAsync(id, _project, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task MutateAsync(JsonPatchBuilder patch, CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            Item = await store.UpdateFieldsAsync(id, patch, _project, ct).ConfigureAwait(false);
            var analysis = HtmlMarkdown.Analyze(Item.DescriptionHtml);
            DescriptionMarkdown = analysis.Markdown;
            DescriptionLossy = analysis.Lossy;
        }, ct).ConfigureAwait(false);
    }

    private async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!AdoExceptions.IsTimeout(ex, ct))
        {
            throw; // genuine user/dialog cancel (carries our token) stays silent
        }
        catch (Exception ex) when (ex is OperationCanceledException || AdoExceptions.IsExpected(ex))
        {
            // A cancellation reaching here carries a foreign token → an HttpClient timeout,
            // surfaced as an expected error rather than a silent no-data pane (L2).
            Error = ex is OperationCanceledException ? AdoExceptions.TimeoutMessage : ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
