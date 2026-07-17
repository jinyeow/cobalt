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
    private readonly ShellViewModel _vm;
    private readonly WorkItemStoreAdapter? _workItems;
    private readonly PullRequestStoreAdapter? _pullRequests;
    private readonly EditorService _editor;
    private readonly ITextInput _textInput;
    private readonly KeyBindingTable _bindings = KeyBindingTable.Shared;
    private readonly KeymapRouter _router;

    private WorkItemListView? _workItemList;
    private PrListView? _prList;
    private Label? _placeholder;
    // CACHE-1: one enricher for the shell's lifetime so its comment-count cache survives section
    // toggles (the PR screen is kept alive, but the field makes the shared cache explicit).
    private PrCommentCountEnricher? _prEnricher;

    private readonly Label _tabs;
    private readonly View _content;
    private readonly Label _status;
    private readonly Label _message;
    private readonly Label _palettePrompt;
    private readonly TextField _palette;

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
        IOsThemeMonitor? themeMonitor = null)
    {
        _app = app;
        _vm = vm;
        _workItems = workItems;
        _pullRequests = pullRequests;
        _context = context;
        _themeMonitor = themeMonitor;
        _editor = editor ?? new EditorService(new ProcessEditorLauncher(
            Environment.GetEnvironmentVariable, TerminalGuiSuspender.For(app)));
        // In-TUI text entry for comments/replies (ADR 0020) — no $EDITOR handoff. It holds
        // _editor for its Ctrl-E escape hatch; descriptions and tags still use _editor directly.
        _textInput = new TuiTextInput(_app, _editor);
        _router = new KeymapRouter(_bindings);

        Title = "cobalt";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.None;

        _tabs = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _content = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2), CanFocus = true };
        _status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        _message = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        _palettePrompt = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = 1, Height = 1, Text = ":", Visible = false };
        _palette = new TextField { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Visible = false };

        Add(_tabs, _content, _status, _message, _palettePrompt, _palette);

        WireViewModel();
        WireKeys();
        WirePalette();
        WireTheme();
        ShowSection();
        RefreshChrome();
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

    private void WireViewModel()
    {
        _vm.SectionChanged += () => { ShowSection(); RefreshChrome(); };
        _vm.Messages.Changed += RefreshChrome;
        _vm.QuitRequested += _app.RequestStop;
        _vm.HelpRequested += ShowHelp;
        _vm.MessagesRequested += ShowMessages;
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
            if (decision.Command is { } command)
            {
                Dispatch(command, result.Count);
            }
        };
    }

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
            if (WorkItemsActive)
            {
                _workItemList?.Navigate(command, count);
                moved = _workItemList;
            }
            else if (PullRequestsActive)
            {
                _prList?.Navigate(command, count);
                moved = _prList;
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
        _app.Invoke(refresh);
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
        new PrDetailDialog(_app, detailVm, _textInput, _vm.Messages.Info, _pullRequests, _context).Show();
        _prList?.RefreshAfterMutation(); // reflect any votes/edits back into the list, dropping stale cache
    }

    private void OpenWorkItemDetail(long id, string? project)
    {
        if (_workItems is null)
        {
            return;
        }
        var detailVm = new WorkItemDetailViewModel(_workItems, id, project);
        new WorkItemDetailDialog(_app, detailVm, _editor, _vm.Messages.Info, _textInput).Show();
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
        if (_themeMonitor is not null)
        {
            _themeMonitor.Changed += OnOsThemeChanged;
            _themeMonitor.Start();
        }
    }

    /// <summary>:theme dark|light|system — apply the resolved preset (following the OS for System) and repaint.</summary>
    private void OnThemeChangeRequested(ThemeChoice choice)
    {
        var os = _themeMonitor?.Current ?? OsTheme.Unknown;
        ApplyPreset(ThemeResolver.Resolve(choice, os));
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
    private void OnOsThemeChanged(OsTheme os) => _app.Invoke(() => ApplyOsFollow(os));

    /// <summary>
    /// Apply the OS-follow decision synchronously: when following the system, re-resolve for
    /// <paramref name="os"/> and repaint; otherwise no-op. Split from <see cref="OnOsThemeChanged"/>
    /// (which only marshals it onto the UI thread) so the follow behaviour is testable headlessly.
    /// </summary>
    internal void ApplyOsFollow(OsTheme os)
    {
        if (OsFollowPreset(_vm.CurrentTheme, os) is not { } preset)
        {
            return;
        }
        ApplyPreset(preset);
    }

    /// <summary>
    /// The preset to apply when the OS theme flips to <paramref name="os"/>, or
    /// <see langword="null"/> when the user isn't following the system — a fixed <c>dark</c>/
    /// <c>light</c> choice ignores OS changes. Pure so the follow decision is testable headlessly.
    /// </summary>
    internal static ThemePreset? OsFollowPreset(ThemeChoice current, OsTheme os) =>
        current == ThemeChoice.System ? ThemeResolver.Resolve(ThemeChoice.System, os) : null;

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
        };
    }

    private void OpenPalette()
    {
        _message.Visible = false;
        _palettePrompt.Visible = true;
        _palette.Text = "";
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
            _content.Remove(_activeScreen);
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

        _content.Add(_activeScreen);
        // Re-establish focus: swapping the shown screen leaves focus dangling, which stops
        // Window.KeyDown from routing subsequent keys.
        _activeScreen.SetFocus();
    }

    private WorkItemListView BuildWorkItemList(WorkItemStoreAdapter workItems)
    {
        // Seed the view-model with the shell's active filters so the first load honours :done / :project.
        var listVm = new WorkItemListViewModel(workItems, _vm.IncludeCompletedWorkItems, _vm.ProjectFilter);
        var view = new WorkItemListView(_app, listVm);
        view.ItemActivated += OpenWorkItemDetail;
        view.Load();
        return view;
    }

    private PrListView BuildPrList(PullRequestStoreAdapter pullRequests)
    {
        var listVm = new PrListViewModel(pullRequests) { ProjectFilter = _vm.ProjectFilter ?? "" };
        _prEnricher ??= new PrCommentCountEnricher(async (pr, ct) =>
        {
            var threads = await pullRequests.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct)
                .ConfigureAwait(false);
            return threads.Sum(t => t.Comments.Count(c => !c.IsSystem));
        });
        var view = new PrListView(_app, listVm, _prEnricher);
        view.ItemActivated += OpenPrDetail;
        view.Load();
        return view;
    }

    private void RefreshChrome()
    {
        var wi = _vm.ActiveSection == AppSection.WorkItems ? "[Work Items]" : " Work Items ";
        var pr = _vm.ActiveSection == AppSection.PullRequests ? "[Pull Requests]" : " Pull Requests ";
        var current = _vm.Messages.Current;
        // SHELL-2: set + dirty each fixed-layout chrome label only when its text actually changed,
        // instead of dirtying all three on every routine status/log message. Most messages change
        // only the message line, so the tab strip and status line usually stay clean.
        SetIfChanged(_tabs, $" {wi} {pr}");
        SetIfChanged(_status, _vm.StatusLine);
        SetIfChanged(_message, current is null ? "" : $" {current.Text}");
    }

    /// <summary>Sets a chrome label's text and marks it dirty only when it changed; returns whether
    /// it changed. Keeps <see cref="RefreshChrome"/> from repainting labels whose text is identical.</summary>
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

    private void ShowHelp() => TextDialog.Show(_app, "keys", HelpText.For(_bindings, ActiveScope));

    private void ShowMessages()
    {
        var lines = _vm.Messages.History
            .Select(m => $"{m.At:HH:mm:ss} {(m.Level == MessageLevel.Error ? "E" : "I")} {m.Text}");
        TextDialog.Show(_app, "messages", string.Join("\n", lines));
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
            if (_themeMonitor is not null)
            {
                _themeMonitor.Changed -= OnOsThemeChanged;
            }
            // CACHE-1: the persistent list screens are kept alive across section switches, so the
            // hidden one is not in the view tree and would not be disposed by the base Window.
            // Remove the visible one first (base disposes the tree), then dispose both once.
            if (_activeScreen is not null)
            {
                _content.Remove(_activeScreen);
                _activeScreen = null;
            }
            _workItemList?.Dispose();
            _prList?.Dispose();
            _placeholder?.Dispose();
        }
        base.Dispose(disposing);
    }
}
