using Cobalt.Tui.Editor;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The work-item verb flows — change state, comment, assign, tags, and edit
/// description — in one place so the detail dialog and the list run the same code
/// (single source of truth). Each flow drives a <see cref="WorkItemDetailViewModel"/>;
/// the dialog passes its own bound view-model (so the pane updates live), while the
/// list entry points (<c>Run…</c>) construct and load a throwaway view-model for the
/// selected row's id.
/// </summary>
public sealed class WorkItemActions
{
    private readonly EditorService _editor;
    private readonly Action<string> _log;
    private readonly Func<string, IReadOnlyList<string>, int?> _choose;
    private readonly Action<Action> _post;

    /// <param name="app">Host application; supplies the default state chooser and UI-thread marshaling.</param>
    /// <param name="editor">Editor used for comment/assign/tags/description text entry.</param>
    /// <param name="log">Message sink for success/failure lines.</param>
    /// <param name="choose">Option picker (title, options) → chosen index, or null/-1 when dismissed; defaults to a <see cref="MessageBox"/> query. Injectable for tests.</param>
    /// <param name="post">Marshals a callback onto the UI thread; defaults to <c>IApplication.Invoke</c>. Injectable for tests.</param>
    public WorkItemActions(
        IApplication app,
        EditorService editor,
        Action<string> log,
        Func<string, IReadOnlyList<string>, int?>? choose = null,
        Action<Action>? post = null)
    {
        _editor = editor;
        _log = log;
        _choose = choose ?? new Func<string, IReadOnlyList<string>, int?>(
            (title, options) => MessageBox.Query(app, title, "", [.. options]));
        _post = post ?? app.Invoke;
    }

    // ---- list entry points: build + load a view-model for the id, then run the flow ----

    public async Task RunCommentAsync(IWorkItemStore store, long id, CancellationToken ct) =>
        await CommentAsync(new WorkItemDetailViewModel(store, id), ct).ConfigureAwait(false);

    public async Task RunChangeStateAsync(IWorkItemStore store, long id, CancellationToken ct)
    {
        var vm = new WorkItemDetailViewModel(store, id);
        await vm.LoadAsync(ct).ConfigureAwait(false);
        await ChangeStateAsync(vm, ct).ConfigureAwait(false);
    }

    public async Task RunAssignAsync(IWorkItemStore store, long id, CancellationToken ct) =>
        await AssignAsync(new WorkItemDetailViewModel(store, id), ct).ConfigureAwait(false);

    public async Task RunTagsAsync(IWorkItemStore store, long id, CancellationToken ct)
    {
        var vm = new WorkItemDetailViewModel(store, id);
        await vm.LoadAsync(ct).ConfigureAwait(false);
        await TagsAsync(vm, ct).ConfigureAwait(false);
    }

    // ---- flows (the single source of truth, shared with the dialog) ----

    public async Task ChangeStateAsync(WorkItemDetailViewModel vm, CancellationToken ct)
    {
        var states = vm.AvailableStates.Select(s => s.Name).ToArray();
        if (states.Length == 0)
        {
            _log("no states available");
            return;
        }
        if (_choose("change state", states) is { } index && index >= 0 && index < states.Length)
        {
            await RunAndLog(vm, vm.ChangeStateAsync(states[index], ct), $"state → {states[index]}").ConfigureAwait(false);
        }
    }

    public async Task CommentAsync(WorkItemDetailViewModel vm, CancellationToken ct)
    {
        var text = await EditAsync("", ".md", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await RunAndLog(vm, vm.AddCommentAsync(text.Trim(), ct), "comment added").ConfigureAwait(false);
        }
    }

    public async Task AssignAsync(WorkItemDetailViewModel vm, CancellationToken ct)
    {
        var value = await EditAsync("", ".txt", ct).ConfigureAwait(false);
        if (value is not null)
        {
            await RunAndLog(vm, vm.AssignAsync(value.Trim(), ct), "assigned").ConfigureAwait(false);
        }
    }

    public async Task TagsAsync(WorkItemDetailViewModel vm, CancellationToken ct)
    {
        var value = await EditAsync(string.Join("; ", vm.Item?.Tags ?? []), ".txt", ct).ConfigureAwait(false);
        if (value is not null)
        {
            var tags = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await RunAndLog(vm, vm.SetTagsAsync(tags, ct), "tags updated").ConfigureAwait(false);
        }
    }

    public async Task EditDescriptionAsync(WorkItemDetailViewModel vm, CancellationToken ct)
    {
        var edited = await EditAsync(vm.DescriptionMarkdown, ".md", ct).ConfigureAwait(false);
        if (edited is not null)
        {
            await RunAndLog(vm, vm.SaveDescriptionAsync(edited, ct), "description saved").ConfigureAwait(false);
        }
    }

    private async Task<string?> EditAsync(string initial, string extension, CancellationToken ct)
    {
        try
        {
            return await _editor.EditAsync(initial, extension, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
        {
            _post(() => _log($"editor failed: {ex.Message}"));
            return null;
        }
    }

    private async Task RunAndLog(WorkItemDetailViewModel vm, Task work, string success)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // caller closed mid-op; nothing to report
        }
        _post(() => _log(vm.Error is { } e ? $"failed: {e}" : success));
    }
}
