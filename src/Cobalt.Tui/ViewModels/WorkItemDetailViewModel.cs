using Cobalt.Core.Ado;
using Cobalt.Core.Models;
using Cobalt.Core.Text;

namespace Cobalt.Tui.ViewModels;

/// <summary>Work-item reads and writes the detail screen needs; abstracted for testing.</summary>
public interface IWorkItemStore
{
    Task<WorkItem> GetWorkItemAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<WorkItemStateDto>> GetStatesAsync(string type, CancellationToken ct);
    Task<WorkItem> UpdateFieldsAsync(long id, JsonPatchBuilder patch, CancellationToken ct);
    Task<WorkItemComment> AddCommentAsync(long id, string text, CancellationToken ct);
}

/// <summary>
/// Work-item detail: fields, HTML-as-Markdown description with a lossiness flag,
/// the comment thread, and the mutations (state, comment, description, fields).
/// </summary>
public sealed class WorkItemDetailViewModel(IWorkItemStore store, long id)
{
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
            Item = await store.GetWorkItemAsync(id, ct).ConfigureAwait(false);

            var analysis = HtmlMarkdown.Analyze(Item.DescriptionHtml);
            DescriptionMarkdown = analysis.Markdown;
            DescriptionLossy = analysis.Lossy;

            Comments = await store.GetCommentsAsync(id, ct).ConfigureAwait(false);
            AvailableStates = await store.GetStatesAsync(Item.WorkItemType, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
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
            await store.AddCommentAsync(id, text, ct).ConfigureAwait(false);
            Comments = await store.GetCommentsAsync(id, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task MutateAsync(JsonPatchBuilder patch, CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            Item = await store.UpdateFieldsAsync(id, patch, ct).ConfigureAwait(false);
            var analysis = HtmlMarkdown.Analyze(Item.DescriptionHtml);
            DescriptionMarkdown = analysis.Markdown;
            DescriptionLossy = analysis.Lossy;
        }).ConfigureAwait(false);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
