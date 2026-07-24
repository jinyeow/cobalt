using Cobalt.Core.Config;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tasks;
using Cobalt.Tui.Theming;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.App;

/// <summary>
/// The top-level window: tab strip, content host, status + message bars, palette
/// line, and the vim key router. All decisions live in <see cref="ShellViewModel"/>;
/// this class only binds state to widgets.
/// </summary>
public sealed class CobaltShell : Window
{
    private readonly IApplication _app;
    // Marshals background continuations onto the UI thread (ADR 0013). Terminal.Gui itself is kept
    // for RequestStop/LayoutAndDraw/Clipboard/dialog construction; only the Invoke sites route here.
    private readonly IUiPost _post;
    private readonly ShellViewModel _vm;
    private readonly WorkItemStoreAdapter? _workItems;
    private readonly PullRequestStoreAdapter? _pullRequests;
    private readonly EditorService _editor;
    private readonly ITextInput _textInput;
    private readonly KeyBindingTable _bindings;
    private readonly KeymapRouter _router;
    private readonly PaletteSuggestionsViewModel _suggestions;
    // Workspace pane focus (ADR 0024): focus is workspace state owned by the view-model,
    // never inferred from Terminal.Gui focus; ApplyWorkspaceFocus is the one place that
    // maps it onto a SetFocus. At M5 nothing sets the preview visible yet (#48 wires the
    // layout), so CyclePane always falls back to today's Tab semantics.
    private readonly WorkspaceViewModel _workspace = new();
    // The text the palette field held after the last Tab/S-Tab completion, so a subsequent Tab
    // cycles the existing suggestions instead of re-filtering; a user edit (field != this) restarts.
    private string? _lastCompletion;

    private WorkItemListView? _workItemList;
    private PrListView? _prList;
    // The list view-models the shell built, kept so the `:` palette can offer their distinct
    // project names for completion and so the empty-state scope accessor stays wired to _vm.Scope.
    private PrListViewModel? _prListVm;
    private WorkItemListViewModel? _workItemListVm;
    private Label? _placeholder;
    // CACHE-1: one enricher for the shell's lifetime so its comment-count cache survives section
    // toggles (the PR screen is kept alive, but the field makes the shared cache explicit).
    private PrCommentCountEnricher? _prEnricher;

    private readonly Label _tabs;
    private readonly View _content;
    // The content area's two children (ADR 0024): the list screen's host on the left, the
    // read-only preview on the right. ApplyWorkspaceLayout sizes both from WorkspaceLayout.
    private readonly View _listHost;
    private readonly PreviewPane _previewPane;
    // The user's preview setting (`preview = auto|off`, flipped live by `:preview`). The width
    // decision stays in WorkspaceLayout; this override is applied on top of it, here.
    private PreviewMode _previewMode;
    // The preview's two-tier load (ADR 0024 / #49) and the workspace-lifetime token every one of
    // its fetches is linked to, so teardown cancels whatever is in flight.
    private readonly PreviewViewModel _preview;
    private readonly CancellationTokenSource _previewLifetime = new();
    // The pane's text width, captured on the UI thread at layout time: a tier-2 fetch completes on
    // a threadpool continuation and must not read a Terminal.Gui viewport from there.
    private int _previewTextWidth = 1;
    private readonly Label _status;
    private readonly Label _message;
    private readonly Label _keybar;
    private readonly Label _palettePrompt;
    private readonly TextField _palette;
    private int _lastChromeWidth = -1;

    private View? _activeScreen;

    private readonly AdoContext? _context;
    private readonly IOsThemeMonitor? _themeMonitor;

    public CobaltShell(
        IApplication app,
        ShellViewModel vm,
        WorkItemStoreAdapter? workItems = null,
        PullRequestStoreAdapter? pullRequests = null,
        EditorService? editor = null,
        AdoContext? context = null,
        IOsThemeMonitor? themeMonitor = null,
        KeyBindingTable? bindings = null,
        IUiPost? post = null,
        TimeProvider? time = null)
    {
        _app = app;
        _post = post ?? new ApplicationUiPost(app);
        _vm = vm;
        _workItems = workItems;
        _pullRequests = pullRequests;
        _context = context;
        _themeMonitor = themeMonitor;
        // The remapped key table (KeyBindingTable.FromConfig(config.Keys)) is injected by
        // CobaltTuiApp; tests and any caller that omits it fall back to the process-wide defaults.
        _bindings = bindings ?? KeyBindingTable.Shared;
        _editor = editor ?? new EditorService(new ProcessEditorLauncher(
            Environment.GetEnvironmentVariable, TerminalGuiSuspender.For(app)));
        // In-TUI text entry for comments/replies (ADR 0020) — no $EDITOR handoff. It holds
        // _editor for its Ctrl-E escape hatch; descriptions and tags still use _editor directly.
        _textInput = new TuiTextInput(_app, _editor);
        _router = new KeymapRouter(_bindings);
        // `:` palette completion (ADR 0022): commands from the parser catalog, plus context names
        // and the distinct project names of whatever the lists have loaded.
        _suggestions = new PaletteSuggestionsViewModel(() => _vm.ContextNames, ProjectNames);

        Title = "cobalt";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.None;

        // Bottom chrome, top to bottom: status, message (palette overlays it), keybar.
        // The keybar owns the last row like lazygit's bottom line — always visible.
        _tabs = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _content = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3), CanFocus = true };
        _listHost = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _previewPane = new PreviewPane
        {
            X = Pos.Right(_listHost),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _content.Add(_listHost, _previewPane);
        _status = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
        _message = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        _keybar = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        _palettePrompt = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = 1, Height = 1, Text = ":", Visible = false };
        _palette = new TextField { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Visible = false };

        Add(_tabs, _content, _status, _message, _keybar, _palettePrompt, _palette);

        // The keybar fits itself to the terminal width, so re-render the chrome when
        // the shell is resized (the list screens do the same for their columns).
        ViewportChanged += (_, _) =>
        {
            if (Viewport.Width != _lastChromeWidth)
            {
                RefreshChrome();
            }
        };
        // The split is driven by the CONTENT area's width, and the shell's own ViewportChanged
        // runs before its children are laid out (the content pane's width is still stale there),
        // so listen to the content pane itself. ApplyWorkspaceLayout refreshes the chrome too —
        // the keybar's Tab entry depends on whether the preview shows.
        _content.ViewportChanged += (_, _) => ApplyWorkspaceLayout();

        _previewMode = vm.CurrentPreview;
        // The preview pipeline (ADR 0024 / #49): tier 1 paints from the row the list already holds,
        // tier 2 fetches a fresh detail view-model per item once the cursor settles.
        _preview = new PreviewViewModel(FetchPreviewDetailAsync, _previewLifetime.Token, time);
        _preview.Changed += OnPreviewChanged;
        WireViewModel();
        WireKeys();
        WirePalette();
        WireTheme();
        ShowSection();
        ApplyWorkspaceLayout();
    }

    public KeyScope ActiveScope => _vm.ActiveSection == AppSection.WorkItems
        ? KeyScope.WorkItemList
        : KeyScope.PullRequestList;

    // With CACHE-1 both list screens are always non-null once built, so the active section — not a
    // null check on the screen — decides which one a shell command targets.
    private bool WorkItemsActive => _vm.ActiveSection == AppSection.WorkItems;
    private bool PullRequestsActive => _vm.ActiveSection == AppSection.PullRequests;

    /// <summary>
    /// Test seam (INPUT-1): how a vim-movement repaint is issued, so a headless test can observe the
    /// force flag without a full <see cref="IApplication"/> fake. Defaults to a non-forced
    /// <see cref="IApplication.LayoutAndDraw(bool)"/>; production never sets it.
    /// </summary>
    internal Action<bool>? MovementRedrawOverride { get; set; }

    /// <summary>Test seam: the persistent PR list screen (kept alive across section switches, CACHE-1).</summary>
    internal PrListView? PrListScreen => _prList;

    /// <summary>Test seam: the persistent work-item list screen (kept alive across section switches, CACHE-1).</summary>
    internal WorkItemListView? WorkItemListScreen => _workItemList;

    /// <summary>Test seam: the rendered bottom keybar text.</summary>
    internal string KeybarText => _keybar.Text;

    /// <summary>Test seam: the PR list's view-model (built lazily on first PR-section show).</summary>
    internal PrListViewModel? PrListVm => _prListVm;

    /// <summary>Test seam: the work-item list's view-model (built lazily on first work-items show).</summary>
    internal WorkItemListViewModel? WorkItemListVm => _workItemListVm;

    /// <summary>Test seam: opens the `:` palette (headless palette-completion tests drive the field directly).</summary>
    internal TextField OpenPaletteForTest()
    {
        OpenPalette();
        return _palette;
    }

    private void WireViewModel()
    {
        _vm.SectionChanged += () => { ShowSection(); RefreshChrome(); };
        // A routine log entry only changes the message row; rebuilding the keybar and
        // tab strip per message would undo the "no whole-chrome work per status
        // message" perf posture (see RefreshChrome).
        _vm.Messages.Changed += RefreshMessage;
        _vm.QuitRequested += _app.RequestStop;
        _vm.HelpRequested += ShowHelp;
        _vm.MessagesRequested += ShowMessages;
        _vm.LogRequested += ShowLog;
        _vm.PickContextRequested += () =>
            _vm.Messages.Info($"contexts: {string.Join(", ", _vm.ContextNames)} — switch with :context NAME");
        _vm.ContextSwitchRequested += name =>
        {
            // Real reconnection lands with the data screens (M3); update chrome now.
            _vm.OnContextSwitched(name, _vm.UserName);
            RefreshChrome();
        };
        _vm.ScopeChangeRequested += scope =>
        {
            // Repoint both adapters and reload the active list against the new breadth.
            if (_pullRequests is not null)
            {
                _pullRequests.Scope = scope;
            }
            if (_workItems is not null)
            {
                _workItems.Scope = scope;
            }
            // A scope flip changes the row set entirely (org ↔ project); reload from the top.
            _prList?.ReloadFromTop();
            _workItemList?.OnRefresh();
            RefreshChrome();
        };
        _vm.DoneFilterChanged += include => _workItemList?.SetIncludeCompleted(include);
        _vm.ProjectFilterChanged += project =>
        {
            _workItemList?.SetProjectFilter(project); // server-side re-query
            _prList?.SetProjectFilter(project);       // client-side narrowing
        };
    }

    private void WireKeys()
    {
        KeyDown += (_, key) =>
        {
            if (_palette.Visible)
            {
                return; // palette input handles its own keys
            }

            var token = KeyTokenizer.ToToken(key);
            if (token is null)
            {
                return;
            }

            var result = _router.Feed(token, ActiveScope);
            var decision = DecideKey(token, result);
            if (decision.Handled)
            {
                key.Handled = true;
            }
            // Refresh the showcmd before dispatching: the router's pending state is
            // already final for this key (count armed, chord started, Esc cleared,
            // match consumed). A movement dispatch now does a non-forced redraw
            // (INPUT-1), but RefreshStatus has already marked the status row dirty on a
            // pending-count change, so LayoutAndDraw(false) still repaints it without the
            // stale count.
            RefreshStatus();
            if (decision.Command is { } command)
            {
                Dispatch(command, result.Count);
            }
        };
    }

    /// <summary>Chrome width: the live viewport, or a standard width before first layout.</summary>
    private int ChromeWidth => Viewport.Width > 0 ? Viewport.Width : 80;

    /// <summary>Re-renders only the status row (left text + right-aligned showcmd), when it changed.</summary>
    private void RefreshStatus()
    {
        var composed = StatusLineComposer.Compose(_vm.StatusLine, _router.PendingDisplay, ChromeWidth);
        if (composed == _status.Text)
        {
            return; // held j/k fires this per keystroke — don't churn the draw loop
        }
        _status.Text = composed;
        _status.SetNeedsDraw();
    }

    /// <summary>Re-renders only the message row — the only chrome a routine log entry changes.</summary>
    private void RefreshMessage()
    {
        var current = _vm.Messages.Current;
        // SHELL-2: set + dirty only when the text actually changed, so a repeated/identical log
        // entry doesn't churn the draw loop.
        SetIfChanged(_message, current is null ? "" : $" {current.Text}");
    }

    /// <summary>Sets a chrome label's text and marks it dirty only when it changed; returns whether
    /// it changed. Keeps the chrome refresh from repainting labels whose text is identical.</summary>
    internal static bool SetIfChanged(Label label, string text)
    {
        if (string.Equals(label.Text, text, StringComparison.Ordinal))
        {
            return false;
        }
        label.Text = text;
        label.SetNeedsDraw();
        return true;
    }

    /// <summary>Test seam: the rendered status row text.</summary>
    internal string StatusText => _status.Text;

    /// <summary>
    /// Pure decision for a routed key: whether the shell consumes it and which
    /// command (if any) to dispatch. Esc with no binding is consumed here — its job
    /// is to clear a pending sequence, so it must never fall through to the
    /// application-level Quit binding (that was the "Esc quits the app" bug).
    /// </summary>
    internal static KeyDecision DecideKey(string token, KeyResult result) => result.Kind switch
    {
        KeyResultKind.Pending => new KeyDecision(true, null),
        KeyResultKind.Matched => new KeyDecision(true, result.Command),
        _ => new KeyDecision(token == "Esc", null),
    };

    internal readonly record struct KeyDecision(bool Handled, AppCommand? Command);

    private void Dispatch(AppCommand command, int? count = null)
    {
        // Workspace Tab (ADR 0024): with a visible preview, Tab cycles pane focus and is
        // consumed; while the preview is hidden the workspace declines it and Tab keeps
        // exactly today's semantics (the PR sub-tab intercept / section toggle below).
        if (command == AppCommand.CyclePane)
        {
            if (_workspace.CyclePane())
            {
                ApplyWorkspaceFocus();
                return;
            }
            Dispatch(AppCommand.NextTab, count);
            return;
        }

        // C-h / C-l move workspace pane focus. When nothing changes (preview hidden, or
        // already at that edge) fall through so the keys keep their current behaviour.
        if (command is AppCommand.FocusLeft or AppCommand.FocusRight)
        {
            var changed = command == AppCommand.FocusLeft ? _workspace.FocusLeft() : _workspace.FocusRight();
            if (changed)
            {
                ApplyWorkspaceFocus();
                return;
            }
        }

        // In the PR section (with a built list), Tab/S-Tab cycle the PR sub-tabs (review
        // queue/team/mine/active) rather than switching top-level sections; section switches go
        // through the g-chords (gt/gT/g1/g2), handled by _vm.HandleCommand below. When the PR list
        // isn't built (no connection → placeholder), fall through so Tab still toggles sections.
        if (PullRequestsActive && _prList is not null && command is AppCommand.NextTab or AppCommand.PrevTab)
        {
            if (command == AppCommand.NextTab)
            {
                _prList.NextTab();
            }
            else
            {
                _prList.PrevTab();
            }
            return;
        }

        // Vim movement: the router matched and consumed the key, so forward it to the
        // active section's list only (both are kept alive now, so a null check no longer
        // identifies the visible one). ListView only navigates on arrow keys natively.
        if (VimScroll.Applies(command))
        {
            View? moved = null;
            // The workspace decides where movement lands (ADR 0024): the preview scrolls while
            // it holds focus, the list cursor moves otherwise. Without this the focused preview
            // would be a trap that j/k cannot move.
            if (_workspace.Route(command) == WorkspaceKeyRoute.PreviewScroll)
            {
                _previewPane.Scroll(command, count);
                moved = _previewPane;
            }
            else if (WorkItemsActive)
            {
                _workItemList?.Navigate(command, count);
                moved = _workItemList;
                UpdatePreview(); // the cursor moved: tier 1 repaints now, tier 2 waits for the settle
            }
            else if (PullRequestsActive)
            {
                _prList?.Navigate(command, count);
                moved = _prList;
                UpdatePreview();
            }
            // INPUT-1: dirty only the moved list and issue a non-forced layout+draw, instead of
            // forcing a full-app repaint on every keystroke. A programmatic InvokeCommand move may
            // not flag the view dirty on its own, so SetNeedsDraw supplies that flag explicitly —
            // which is what the old force:true was compensating for (ADR 0016). UAT-gated on both
            // the windows and dotnet drivers.
            moved?.SetNeedsDraw();
            (MovementRedrawOverride ?? _app.LayoutAndDraw)(false);
            return;
        }

        if (_vm.HandleCommand(command))
        {
            return;
        }

        switch (command)
        {
            case AppCommand.CommandPalette:
                OpenPalette();
                break;
            case AppCommand.Refresh:
                // `r` forces a fresh load of the visible section only (CACHE-1 keeps the other's
                // rows as-is until it is next shown or refreshed).
                if (WorkItemsActive)
                {
                    _workItemList?.OnRefresh();
                }
                else if (PullRequestsActive)
                {
                    _prList?.Refresh();
                }
                break;
            case AppCommand.FilterStart:
                if (WorkItemsActive)
                {
                    _workItemList?.StartFiltering();
                }
                break;
            case AppCommand.Open:
                if (WorkItemsActive)
                {
                    _workItemList?.OnOpen();
                }
                else if (PullRequestsActive)
                {
                    _prList?.OnOpen();
                }
                break;
            case AppCommand.YankId:
                CopyCurrentUrl();
                break;
            case AppCommand.OpenInBrowser:
                OpenCurrentInBrowser();
                break;
            case AppCommand.Comment:
                RunWorkItemAction((a, store, id, project, ct) => a.RunCommentAsync(store, id, project, ct));
                break;
            case AppCommand.ChangeState:
                RunWorkItemAction((a, store, id, project, ct) => a.RunChangeStateAsync(store, id, project, ct));
                break;
            case AppCommand.Assign:
                RunWorkItemAction((a, store, id, project, ct) => a.RunAssignAsync(store, id, project, ct));
                break;
            case AppCommand.EditTags:
                RunWorkItemAction((a, store, id, project, ct) => a.RunTagsAsync(store, id, project, ct));
                break;
            case AppCommand.Vote:
                RunPrVote();
                break;
            default:
                // Router matched the key but nothing in this context handles it — surface
                // it instead of swallowing (the original "c/t do nothing" complaint).
                _vm.Messages.Info($"'{KeyLabel(command)}' not available here");
                break;
        }
    }

    /// <summary>The first key sequence bound to <paramref name="command"/> in the active scope, for messages.</summary>
    private string KeyLabel(AppCommand command)
    {
        foreach (var (sequence, cmd) in _bindings.Visible(ActiveScope))
        {
            if (cmd == command)
            {
                return string.Join("", sequence);
            }
        }
        return command.ToString();
    }

    private void RunWorkItemAction(Func<WorkItemActions, IWorkItemStore, long, string?, CancellationToken, Task> run)
    {
        if (_workItems is null)
        {
            return;
        }
        if (_workItemList?.SelectedId is not { } id)
        {
            _vm.Messages.Info("no work item selected");
            return;
        }
        // The highlighted row's project (org scope may span projects); the detail/mutation
        // path targets it, not the context project (H1).
        var project = _workItemList.SelectedProject;
        var actions = new WorkItemActions(_app, _editor, _vm.Messages.Info, _textInput);
        _ = RunThenRefreshAsync(run(actions, _workItems, id, project, CancellationToken.None), () => _workItemList?.OnRefresh());
    }

    private void RunPrVote()
    {
        if (_pullRequests is null)
        {
            return;
        }
        if (_prList?.SelectedPr is not { } pr)
        {
            _vm.Messages.Info("no pull request selected");
            return;
        }
        var actions = new PrActions(_app, _vm.Messages.Info);
        _ = RunThenRefreshAsync(actions.RunVoteAsync(_pullRequests, pr.PullRequestId, CancellationToken.None), () => _prList?.RefreshAfterMutation());
    }

    private async Task RunThenRefreshAsync(Task action, Action refresh)
    {
        await action.IgnoreCancellationAsync().ConfigureAwait(false);
        _post.Post(refresh);
    }

    private string? CurrentUrl()
    {
        if (_context is null)
        {
            return null;
        }
        // Both screens are kept alive, so yank/open the selection in the visible section only.
        if (WorkItemsActive && _workItemList?.SelectedId is { } wid)
        {
            return AdoUrls.WorkItem(_context, wid, _workItemList.SelectedProject);
        }
        if (PullRequestsActive && _prList?.SelectedPr is { } pr)
        {
            return AdoUrls.PullRequest(_context, pr.ProjectName, pr.RepositoryName, pr.PullRequestId);
        }
        return null;
    }

    private void CopyCurrentUrl()
    {
        if (CurrentUrl() is not { } url)
        {
            _vm.Messages.Info("nothing to yank — select a work item or PR first");
            return;
        }
        if (_app.Clipboard?.TrySetClipboardData(url) == true)
        {
            _vm.Messages.Info($"yanked {url}");
        }
        else
        {
            _vm.Messages.Info($"clipboard unavailable — {url}");
        }
    }

    private void OpenCurrentInBrowser()
    {
        if (CurrentUrl() is not { } url)
        {
            _vm.Messages.Info("nothing to open — select a work item or PR first");
            return;
        }
        if (BrowserLauncher.TryOpen(url, out var error))
        {
            _vm.Messages.Info($"opened {url}");
        }
        else
        {
            _vm.Messages.Error($"could not open browser: {error}");
        }
    }

    private void OpenPrDetail(int id)
    {
        if (_pullRequests is null)
        {
            return;
        }
        var detailVm = new PrDetailViewModel(_pullRequests, id);
        new PrDetailDialog(_app, detailVm, _textInput, _vm.Messages.Info, _pullRequests, _context, _bindings, _post).Show();
        _prList?.RefreshAfterMutation(); // reflect any votes/edits back into the list, dropping stale cache
    }

    private void OpenWorkItemDetail(long id, string? project)
    {
        if (_workItems is null)
        {
            return;
        }
        var detailVm = new WorkItemDetailViewModel(_workItems, id, project);
        new WorkItemDetailDialog(_app, detailVm, _editor, _vm.Messages.Info, _textInput, _bindings, _post).Show();
        _workItemList?.OnRefresh(); // reflect any edits back into the list
    }

    /// <summary>
    /// Wires live theming: <c>:theme</c> re-resolves the preset and repaints, and (when following
    /// the system) an OS light/dark flip does the same. The palette/scheme swap needs no view
    /// recreation — a forced repaint re-resolves every view's SchemeName and the diff's ambient
    /// palette.
    /// </summary>
    private void WireTheme()
    {
        _vm.ThemeChangeRequested += OnThemeChangeRequested;
        _vm.PreviewChangeRequested += OnPreviewChangeRequested;
        if (_themeMonitor is not null)
        {
            _themeMonitor.Changed += OnOsThemeChanged;
            _themeMonitor.Start();
        }
    }

    /// <summary>:preview auto|off — re-apply the split with the new override. The setting lives in
    /// <c>config.toml</c>, which cobalt never writes back; this switches the running session only.</summary>
    private void OnPreviewChangeRequested(PreviewMode mode)
    {
        _previewMode = mode;
        ApplyWorkspaceLayout();
    }

    /// <summary>:theme dark|light|system — apply the resolved preset (following the OS for System) and repaint.</summary>
    private void OnThemeChangeRequested(ThemeChoice choice)
    {
        var os = _themeMonitor?.Current ?? OsTheme.Unknown;
        // Honour the detected colour tier (ADR 0019 extension) so a live :theme switch degrades
        // the diff palette in step with the chrome, exactly as startup does.
        ApplyPreset(ThemeResolver.Resolve(choice, os, ThemeService.Capabilities.Color));
    }

    /// <summary>Apply a theme preset and force a full repaint so every view re-resolves its scheme
    /// and the diff its ambient palette. Shared by the <c>:theme</c> command and the OS-follow path.</summary>
    private void ApplyPreset(ThemePreset preset)
    {
        ThemeService.Apply(preset);
        _app.LayoutAndDraw(true);
    }

    /// <summary>
    /// The OS light/dark setting changed. Only acts while following the system
    /// (<c>theme = system</c>). Raised from the monitor's watcher thread, so marshal onto the UI
    /// thread before touching Terminal.Gui.
    /// </summary>
    private void OnOsThemeChanged(OsTheme os) => _post.Post(() => ApplyOsFollow(os));

    /// <summary>
    /// Apply the OS-follow decision synchronously: when following the system, re-resolve for
    /// <paramref name="os"/> and repaint; otherwise no-op. Split from <see cref="OnOsThemeChanged"/>
    /// (which only marshals it onto the UI thread) so the follow behaviour is testable headlessly.
    /// </summary>
    internal void ApplyOsFollow(OsTheme os)
    {
        if (OsFollowPreset(_vm.CurrentTheme, os, ThemeService.Capabilities.Color) is not { } preset)
        {
            return;
        }
        ApplyPreset(preset);
    }

    /// <summary>
    /// The preset to apply when the OS theme flips to <paramref name="os"/>, or
    /// <see langword="null"/> when the user isn't following the system — a fixed <c>dark</c>/
    /// <c>light</c> choice ignores OS changes. Takes the detected <paramref name="color"/> tier so
    /// the follow decision degrades the diff palette (ADR 0019 extension). Pure so it is testable
    /// headlessly.
    /// </summary>
    internal static ThemePreset? OsFollowPreset(ThemeChoice current, OsTheme os, ColorSupport color) =>
        current == ThemeChoice.System ? ThemeResolver.Resolve(ThemeChoice.System, os, color) : null;

    private void WirePalette()
    {
        _palette.KeyDown += (_, key) =>
        {
            if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Enter)
            {
                var input = _palette.Text?.ToString() ?? "";
                ClosePalette();
                _vm.HandlePaletteInput(input);
                key.Handled = true;
            }
            else if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Esc)
            {
                ClosePalette();
                key.Handled = true;
            }
            else if (key.KeyCode == Terminal.Gui.Drivers.KeyCode.Tab)
            {
                CompletePalette(forward: true);
                key.Handled = true; // else Tab would move focus off the field
            }
            else if (key.KeyCode == (Terminal.Gui.Drivers.KeyCode.Tab | Terminal.Gui.Drivers.KeyCode.ShiftMask))
            {
                CompletePalette(forward: false);
                key.Handled = true;
            }
        };
    }

    /// <summary>
    /// One Tab/S-Tab step of `:` completion. Re-ranks suggestions for the current text when the
    /// field changed since the last completion, OR when the last completion left a trailing space —
    /// that space is how <see cref="PaletteSuggestionsViewModel.Accept"/> marks "this command takes
    /// an argument, keep completing", so the next Tab must chain into ARGUMENT candidates (e.g.
    /// <c>:context </c> → context names) rather than re-cycle the command list. Otherwise the field
    /// is a settled candidate and Tab/S-Tab cycles the standing suggestions. The completed text
    /// (leading colon and argument spacing preserved by Accept) is written back and the caret moved
    /// to the end so the next Tab keeps completing.
    /// </summary>
    private void CompletePalette(bool forward)
    {
        var text = _palette.Text?.ToString() ?? "";
        var chainIntoArgument = text == _lastCompletion && text.EndsWith(' ');
        if (text != _lastCompletion || chainIntoArgument)
        {
            _suggestions.SetInput(text);
            if (!forward)
            {
                _suggestions.CyclePrev(); // reverse-completing a fresh field lands on the last match
            }
        }
        else if (forward)
        {
            _suggestions.CycleNext();
        }
        else
        {
            _suggestions.CyclePrev();
        }

        var completed = _suggestions.Accept();
        _palette.Text = completed;
        _palette.InsertionPoint = completed.Length;
        _lastCompletion = completed;
    }

    /// <summary>Distinct project names across whatever the two lists have loaded — the `:project` completion pool.</summary>
    private IReadOnlyList<string> ProjectNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_prListVm is not null)
        {
            foreach (var pr in _prListVm.Rows)
            {
                if (pr.ProjectName.Length != 0)
                {
                    names.Add(pr.ProjectName);
                }
            }
        }
        if (_workItemListVm is not null)
        {
            foreach (var wi in _workItemListVm.Rows)
            {
                if (wi.TeamProject.Length != 0)
                {
                    names.Add(wi.TeamProject);
                }
            }
        }
        return [.. names];
    }

    private void OpenPalette()
    {
        _message.Visible = false;
        _palettePrompt.Visible = true;
        _palette.Text = "";
        _lastCompletion = null; // a fresh palette always re-filters on the first Tab
        _palette.Visible = true;
        _palette.SetFocus();
    }

    private void ClosePalette()
    {
        _palette.Visible = false;
        _palettePrompt.Visible = false;
        _message.Visible = true;
        _activeScreen?.SetFocus();
    }

    private void ShowSection()
    {
        // CACHE-1: keep the list screens alive across section switches — remove the current one
        // from the content host but do NOT dispose it, so switching back reuses it (rows stay as
        // last loaded until an explicit refresh) instead of refetching. The screens are built
        // lazily once and disposed with the shell.
        if (_activeScreen is not null)
        {
            _listHost.Remove(_activeScreen);
            _activeScreen = null;
        }

        if (_vm.ActiveSection == AppSection.WorkItems && _workItems is not null)
        {
            _activeScreen = _workItemList ??= BuildWorkItemList(_workItems);
        }
        else if (_vm.ActiveSection == AppSection.PullRequests && _pullRequests is not null)
        {
            _activeScreen = _prList ??= BuildPrList(_pullRequests);
        }
        else
        {
            // No connection: show a hint instead of an empty pane.
            _activeScreen = _placeholder ??= new Label
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(),
                Text = "no Azure DevOps connection — run: cobalt auth login",
            };
        }

        _listHost.Add(_activeScreen);
        // Re-establish focus: swapping the shown screen leaves focus dangling, which stops
        // Window.KeyDown from routing subsequent keys.
        ApplyWorkspaceFocus();
        // The other section's selection is a different item — repaint the preview for this one.
        UpdatePreview();
    }

    /// <summary>
    /// Applies the workspace split (ADR 0024): the geometry comes only from the pure
    /// <see cref="WorkspaceLayout.Compute"/>, and the user's <c>preview = off</c> override is
    /// layered on here — never threaded into the width calculator. Runs on construction, on
    /// resize, and on <c>:preview</c>.
    /// </summary>
    private void ApplyWorkspaceLayout()
    {
        var panes = WorkspaceLayout.Compute(_content.Viewport.Width);
        var showPreview = panes.ShowPreview && _previewMode != PreviewMode.Off;

        _workspace.SetPreviewVisible(showPreview);
        _listHost.Width = showPreview ? panes.ListWidth : Dim.Fill();
        _previewPane.Visible = showPreview;
        // One column goes to the vertical scrollbar the pane renders once its content overflows
        // (PreviewPane sets ScrollBars = true); headless never draws, so only the both-driver UAT
        // (ADR 0016) can see it.
        _previewTextWidth = Math.Max(1, panes.PreviewWidth - 1);
        // A `:preview` toggle changes the split without a resize, so re-lay the content area
        // here rather than waiting for the next event that happens to trigger layout.
        _content.Layout();
        ApplyWorkspaceFocus();
        // The keybar advertises Tab only while the preview shows, so the chrome follows the split.
        RefreshChrome();
        // A preview that just appeared has nothing in it until the selection is pushed through.
        UpdatePreview();
    }

    /// <summary>
    /// The single <see cref="WorkspaceViewModel.FocusedPane"/> → Terminal.Gui SetFocus
    /// mapping (ADR 0024: the shell maps workspace focus in exactly one place).
    /// </summary>
    private void ApplyWorkspaceFocus()
    {
        if (_workspace.FocusedPane == WorkspacePane.Preview)
        {
            _previewPane.SetFocus();
            return;
        }
        _activeScreen?.SetFocus();
    }

    /// <summary>
    /// Pushes the highlighted row into the preview (ADR 0024): tier 1 paints from the row's own
    /// data before this returns, and the debounced tier-2 fetch is scheduled behind it. Called on
    /// every cursor move, section switch, layout pass and list load — re-showing the item already
    /// on screen is a no-op inside the view-model, so calling it freely is cheap. Nothing is
    /// scheduled while the preview is collapsed: a hidden pane must not spend round-trips.
    /// </summary>
    private void UpdatePreview()
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
        if (WorkItemsActive && _workItems is not null && _workItemList?.SelectedItem is { } item)
        {
            return (new ItemKey(AppSection.WorkItems, item.Id, _workItemList.SelectedProject),
                WorkItemDetailFormatter.Render(
                    new WorkItemDetailViewModel(_workItems, item), _previewTextWidth, PreviewTier.Summary));
        }
        if (PullRequestsActive && _pullRequests is not null && _prList?.SelectedPr is { } pr)
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
    private void OnListChanged() => _post.Post(UpdatePreview);

    /// <summary>A publish landed — repaint on the UI thread (tier 2 completes on a threadpool
    /// continuation, so this is the ADR 0004 marshalling seam).</summary>
    private void OnPreviewChanged() => _post.Post(RenderPreview);

    /// <summary>The one place preview state becomes pane text: a single snapshot read, so the
    /// key and its text can never be read from two different publishes.</summary>
    private void RenderPreview() => _previewPane.SetContent(_preview.Current?.Text ?? "");

    /// <summary>Test seam: the workspace pane-focus view-model (ADR 0024).</summary>
    internal WorkspaceViewModel Workspace => _workspace;

    /// <summary>Test seam: the read-only preview pane beside the list.</summary>
    internal PreviewPane PreviewScreen => _previewPane;

    private WorkItemListView BuildWorkItemList(WorkItemStoreAdapter workItems)
    {
        // Seed the view-model with the shell's active filters so the first load honours :done / :project.
        // scope: () => _vm.Scope keeps the empty-state hint honest — it only suggests :scope org when
        // the shell is actually project-scoped (a no-op suggestion in org scope is worse than none).
        var listVm = new WorkItemListViewModel(
            workItems, _vm.IncludeCompletedWorkItems, _vm.ProjectFilter, () => _vm.Scope);
        _workItemListVm = listVm;
        // Rows arriving (first load, refresh, filter re-query) change what the cursor sits on
        // without a keystroke, so the preview follows the row set too. Raised on a threadpool
        // continuation → marshal, and re-showing the same item is a no-op. Named so Dispose can
        // detach it before the preview is torn down.
        listVm.Changed += OnListChanged;
        var view = new WorkItemListView(_post, listVm);
        view.ItemActivated += OpenWorkItemDetail;
        view.Load();
        return view;
    }

    private PrListView BuildPrList(PullRequestStoreAdapter pullRequests)
    {
        var listVm = new PrListViewModel(pullRequests, () => _vm.Scope) { ProjectFilter = _vm.ProjectFilter ?? "" };
        _prListVm = listVm;
        listVm.Changed += OnListChanged; // see BuildWorkItemList
        _prEnricher ??= new PrCommentCountEnricher(async (pr, ct) =>
        {
            var threads = await pullRequests.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct)
                .ConfigureAwait(false);
            return threads.Sum(t => t.Comments.Count(c => !c.IsSystem));
        });
        var view = new PrListView(_post, listVm, _prEnricher);
        view.ItemActivated += OpenPrDetail;
        view.Load();
        return view;
    }

    /// <summary>
    /// Full chrome render: tab strip, keybar, status, message. Runs on construction,
    /// section/scope/context changes, and width changes — NOT per log message
    /// (Messages.Changed is wired to the lighter <see cref="RefreshMessage"/>).
    /// </summary>
    private void RefreshChrome()
    {
        _lastChromeWidth = Viewport.Width;
        // SHELL-2: set + dirty each fixed-layout chrome label only when its text actually changed,
        // instead of dirtying the tab strip and keybar on every chrome refresh. RefreshStatus and
        // RefreshMessage apply the same equality guard to their rows.
        SetIfChanged(_tabs, TabStripFormatter.Sections(_vm.ActiveSection));
        SetIfChanged(_keybar, KeybarFormatter.Render(_bindings, ActiveScope, ChromeWidth, _workspace.PreviewVisible));
        RefreshStatus();
        RefreshMessage();
    }

    private void ShowHelp() =>
        TextDialog.Show(_app, "keys", HelpText.For(_bindings, ActiveScope, _workspace.PreviewVisible), _bindings);

    private void ShowMessages()
    {
        var lines = _vm.Messages.History
            .Select(m => $"{m.At:HH:mm:ss} {(m.Level == MessageLevel.Error ? "E" : "I")} {m.Text}");
        TextDialog.Show(_app, "messages", string.Join("\n", lines), _bindings);
    }

    /// <summary>Test seam: replaces the real :log overlay (needs a run loop) so a headless test can
    /// prove the shell is subscribed to <see cref="ShellViewModel.LogRequested"/>.</summary>
    internal Action<OperationLog>? ShowLogOverride { get; set; }

    /// <summary>:log — the ADO operations log (name, masked route, duration, outcome), same overlay as messages.</summary>
    private void ShowLog()
    {
        if (ShowLogOverride is { } show)
        {
            show(_vm.Operations);
            return;
        }
        OperationLogDialog.Show(_app, _vm.Operations, _bindings);
    }

    /// <summary>
    /// Unsubscribe the theme handlers before teardown. The monitor's watcher thread can raise
    /// <see cref="IOsThemeMonitor.Changed"/> late, so detaching here (before the monitor is
    /// disposed by the caller) stops it marshalling onto an app that is shutting down. The
    /// monitor itself is owned and disposed by <c>CobaltTuiApp</c>, not here.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vm.ThemeChangeRequested -= OnThemeChangeRequested;
            _vm.PreviewChangeRequested -= OnPreviewChangeRequested;
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
            if (_themeMonitor is not null)
            {
                _themeMonitor.Changed -= OnOsThemeChanged;
            }
            // CACHE-1: the persistent list screens are kept alive across section switches, so the
            // hidden one is not in the view tree and would not be disposed by the base Window.
            // Remove the visible one first (base disposes the tree), then dispose both once.
            if (_activeScreen is not null)
            {
                _listHost.Remove(_activeScreen);
                _activeScreen = null;
            }
            _content.Remove(_previewPane);
            _previewPane.Dispose();
            _workItemList?.Dispose();
            _prList?.Dispose();
            _placeholder?.Dispose();
        }
        base.Dispose(disposing);
    }
}
