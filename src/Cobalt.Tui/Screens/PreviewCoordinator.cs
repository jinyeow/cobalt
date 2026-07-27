using Cobalt.Tui.App;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The preview pipeline (ADR 0024 / #49): owns the <see cref="PreviewViewModel"/>, its workspace-
/// lifetime cancellation, and the list→pane wiring, so <see cref="CobaltShell"/> only calls into it
/// on selection/layout changes. Lives in <c>Screens/</c> (not <c>ViewModels/</c>) because it reads
/// Terminal.Gui-backed selection off the list views and calls <see cref="PreviewPane.SetContent"/> —
/// <c>ViewModels/</c> is mechanically policed by <c>ViewModelPurityTests</c> per ADR 0004.
/// </summary>
internal sealed class PreviewCoordinator : IDisposable
{
    private readonly PreviewPane _previewPane;
    private readonly WorkspaceViewModel _workspace;
    private readonly ShellViewModel _vm;
    private readonly WorkItemStoreAdapter? _workItems;
    private readonly PullRequestStoreAdapter? _pullRequests;
    private readonly Func<WorkItemListView?> _workItemList;
    private readonly Func<PrListView?> _prList;
    private readonly IUiPost _post;

    // The preview's two-tier load (ADR 0024 / #49) and the workspace-lifetime token every one of
    // its fetches is linked to, so teardown cancels whatever is in flight.
    private readonly PreviewViewModel _preview;
    private readonly CancellationTokenSource _previewLifetime = new();
    // The pane's text width, captured on the UI thread at layout time: a tier-2 fetch completes on
    // a threadpool continuation and must not read a Terminal.Gui viewport from there.
    private int _previewTextWidth = 1;

    private PrListViewModel? _prListVm;
    private WorkItemListViewModel? _workItemListVm;

    public PreviewCoordinator(
        PreviewPane pane,
        WorkspaceViewModel workspace,
        ShellViewModel vm,
        WorkItemStoreAdapter? workItems,
        PullRequestStoreAdapter? pullRequests,
        Func<WorkItemListView?> workItemList,
        Func<PrListView?> prList,
        IUiPost post,
        TimeProvider? time = null)
    {
        _previewPane = pane;
        _workspace = workspace;
        _vm = vm;
        _workItems = workItems;
        _pullRequests = pullRequests;
        _workItemList = workItemList;
        _prList = prList;
        _post = post;
        // The preview pipeline (ADR 0024 / #49): tier 1 paints from the row the list already holds,
        // tier 2 fetches a fresh detail view-model per item once the cursor settles.
        _preview = new PreviewViewModel(FetchPreviewDetailAsync, _previewLifetime.Token, time);
        _preview.Changed += OnPreviewChanged;
    }

    // With CACHE-1 both list screens are always non-null once built, so the active section — not a
    // null check on the screen — decides which one a shell command targets.
    private bool WorkItemsActive => _vm.ActiveSection == AppSection.WorkItems;
    private bool PullRequestsActive => _vm.ActiveSection == AppSection.PullRequests;

    /// <summary>Test seam: the pane's text width, as handed to the detail formatters.</summary>
    internal int TextWidth => _previewTextWidth;

    /// <summary>Stores the already-computed pane text width; no arithmetic here — the shell owns it.</summary>
    public void LayoutChanged(int previewTextWidth) => _previewTextWidth = previewTextWidth;

    /// <summary>Tracks the work-item list view-model's <c>Changed</c> event so a row-set change
    /// refreshes the preview; remembered so <see cref="Dispose"/> can detach it.</summary>
    public void Track(WorkItemListViewModel listVm)
    {
        _workItemListVm = listVm;
        listVm.Changed += OnListChanged;
    }

    /// <summary>Tracks the PR list view-model's <c>Changed</c> event so a row-set change refreshes
    /// the preview; remembered so <see cref="Dispose"/> can detach it.</summary>
    public void Track(PrListViewModel listVm)
    {
        _prListVm = listVm;
        listVm.Changed += OnListChanged;
    }

    /// <summary>
    /// Pushes the highlighted row into the preview (ADR 0024): tier 1 paints from the row's own
    /// data before this returns, and the debounced tier-2 fetch is scheduled behind it. Called on
    /// every cursor move, section switch, layout pass and list load — re-showing the item already
    /// on screen is a no-op inside the view-model, so calling it freely is cheap. Nothing is
    /// scheduled while the preview is collapsed: a hidden pane must not spend round-trips.
    /// </summary>
    public void SelectionChanged()
    {
        if (!_workspace.PreviewVisible)
        {
            // Hidden (collapsed or preview = off): abandon anything armed — a hidden pane must not
            // spend a round-trip. Clear cancels the pending debounce, not merely the paint.
            _preview.Clear();
            return;
        }
        if (CurrentPreviewRow() is not { } row)
        {
            _preview.Clear();
            return;
        }
        // The pipeline's returned task is the shell's to observe (ADR 0013): an unexpected fault in
        // a background preview load reaches the crash log and the message bar, not a discarded task.
        _ = FireAndForget.Observe(_preview.ShowAsync(row.Key, row.Summary), _post, _vm.Messages.Error);
    }

    /// <summary>
    /// The highlighted row of the visible list as (key, tier-1 text): the detail view-model seeded
    /// with the row the list already holds, rendered through the shared formatter's Summary tier —
    /// zero fetches, no second formatter (ADR 0024). Null when nothing is selected.
    /// </summary>
    private (ItemKey Key, string Summary)? CurrentPreviewRow()
    {
        var workItemList = _workItemList();
        var prList = _prList();
        if (WorkItemsActive && _workItems is not null && workItemList?.SelectedItem is { } item)
        {
            return (new ItemKey(AppSection.WorkItems, item.Id, workItemList.SelectedProject),
                WorkItemDetailFormatter.Render(
                    new WorkItemDetailViewModel(_workItems, item), _previewTextWidth, PreviewTier.Summary));
        }
        if (PullRequestsActive && _pullRequests is not null && prList?.SelectedPr is { } pr)
        {
            return (new ItemKey(AppSection.PullRequests, pr.PullRequestId, pr.ProjectName),
                PrDetailFormatter.Render(
                    new PrDetailViewModel(_pullRequests, pr), _previewTextWidth, PreviewTier.Summary));
        }
        return null;
    }

    /// <summary>
    /// Tier 2: a detail view-model built fresh for the previewed item — never shared with the modal
    /// (ADR 0024) — loaded and rendered at the pane's Summary depth. Runs on a background thread;
    /// the width it renders to was captured at layout time.
    /// </summary>
    private async Task<string> FetchPreviewDetailAsync(ItemKey key, CancellationToken ct)
    {
        if (key.Section == AppSection.WorkItems && _workItems is not null)
        {
            var detail = new WorkItemDetailViewModel(_workItems, key.Id, key.Project);
            await detail.LoadAsync(ct).ConfigureAwait(false);
            return WorkItemDetailFormatter.Render(detail, _previewTextWidth, PreviewTier.Summary);
        }
        if (key.Section == AppSection.PullRequests && _pullRequests is not null)
        {
            var detail = new PrDetailViewModel(_pullRequests, (int)key.Id);
            await detail.LoadAsync(ct).ConfigureAwait(false);
            return PrDetailFormatter.Render(detail, _previewTextWidth, PreviewTier.Summary);
        }
        return "";
    }

    /// <summary>A list's row set / selection changed — refresh the preview on the UI thread. Raised
    /// on a background continuation, so marshalled; re-showing the displayed item is a no-op.</summary>
    private void OnListChanged() => _post.Post(SelectionChanged);

    /// <summary>A publish landed — repaint on the UI thread (tier 2 completes on a threadpool
    /// continuation, so this is the ADR 0004 marshalling seam).</summary>
    private void OnPreviewChanged() => _post.Post(RenderPreview);

    /// <summary>The one place preview state becomes pane text: a single snapshot read, so the
    /// key and its text can never be read from two different publishes.</summary>
    private void RenderPreview() => _previewPane.SetContent(_preview.Current?.Text ?? "");

    public void Dispose()
    {
        // Detach the list→preview subscriptions before the preview is torn down, so a list VM
        // that raises Changed during teardown cannot post onto a disposed preview.
        if (_prListVm is not null)
        {
            _prListVm.Changed -= OnListChanged;
        }
        if (_workItemListVm is not null)
        {
            _workItemListVm.Changed -= OnListChanged;
        }
        // Cancel the preview's in-flight fetch before the views it would repaint go away.
        _preview.Changed -= OnPreviewChanged;
        _previewLifetime.Cancel();
        _preview.Dispose();
        _previewLifetime.Dispose();
    }
}
