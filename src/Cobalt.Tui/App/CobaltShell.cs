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
    private readonly KeyBindingTable _bindings = KeyBindingTable.Default();
    private readonly KeymapRouter _router;

    private WorkItemListView? _workItemList;
    private PrListView? _prList;

    private readonly Label _tabs;
    private readonly View _content;
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

        // Bottom chrome, top to bottom: status, message (palette overlays it), keybar.
        // The keybar owns the last row like lazygit's bottom line — always visible.
        _tabs = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _content = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3), CanFocus = true };
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

    /// <summary>Test seam: the rendered bottom keybar text.</summary>
    internal string KeybarText => _keybar.Text;

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
            // match consumed), and a movement dispatch forces a full repaint — the
            // status row must not show the stale count in that frame.
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
        _message.Text = current is null ? "" : $" {current.Text}";
        _message.SetNeedsDraw();
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
        // In the PR section, Tab/S-Tab cycle the PR sub-tabs (review queue/team/mine/active)
        // rather than switching top-level sections; section switches go through the
        // g-chords (gt/gT/g1/g2), handled by _vm.HandleCommand below.
        if (_prList is not null && command is AppCommand.NextTab or AppCommand.PrevTab)
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
        // active list (ListView only navigates on arrow keys natively).
        if (VimScroll.Applies(command))
        {
            _workItemList?.Navigate(command, count);
            _prList?.Navigate(command, count);
            // Force a full redraw now (true, not false): a programmatic InvokeCommand move
            // may not flag the view dirty on every driver, so LayoutAndDraw(false) could
            // skip it and the move would only paint on the next event. (The "needs a second
            // press" symptom under a multiplexer is actually the Win32-console driver dropping
            // input, not a missed paint — see ADR 0016; this forced paint is still correct.)
            _app.LayoutAndDraw(true);
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
                _workItemList?.OnRefresh();
                _prList?.Load();
                break;
            case AppCommand.FilterStart:
                _workItemList?.StartFiltering();
                break;
            case AppCommand.Open:
                _workItemList?.OnOpen();
                _prList?.OnOpen();
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
        _ = RunThenRefreshAsync(actions.RunVoteAsync(_pullRequests, pr.PullRequestId, CancellationToken.None), () => _prList?.Load());
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
        if (_workItemList?.SelectedId is { } wid)
        {
            return AdoUrls.WorkItem(_context, wid, _workItemList.SelectedProject);
        }
        if (_prList?.SelectedPr is { } pr)
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
        _prList?.Load(); // reflect any votes/edits back into the list
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
        if (_activeScreen is not null)
        {
            _content.Remove(_activeScreen);
            _activeScreen.Dispose();
            _activeScreen = null;
            _workItemList = null;
            _prList = null;
        }

        if (_vm.ActiveSection == AppSection.WorkItems && _workItems is not null)
        {
            // Seed the fresh view-model with the shell's active filters so switching away
            // and back doesn't silently drop :done / :project.
            var listVm = new WorkItemListViewModel(_workItems, _vm.IncludeCompletedWorkItems, _vm.ProjectFilter);
            _workItemList = new WorkItemListView(_app, listVm);
            _workItemList.ItemActivated += OpenWorkItemDetail;
            _activeScreen = _workItemList;
            _content.Add(_activeScreen);
            _workItemList.Load();
        }
        else if (_vm.ActiveSection == AppSection.PullRequests && _pullRequests is not null)
        {
            var listVm = new PrListViewModel(_pullRequests) { ProjectFilter = _vm.ProjectFilter ?? "" };
            var store = _pullRequests;
            var enricher = new PrCommentCountEnricher(async (pr, ct) =>
            {
                var threads = await store.GetThreadsAsync(pr.ProjectName, pr.RepositoryId, pr.PullRequestId, ct)
                    .ConfigureAwait(false);
                return threads.Sum(t => t.Comments.Count(c => !c.IsSystem));
            });
            _prList = new PrListView(_app, listVm, enricher);
            _prList.ItemActivated += OpenPrDetail;
            _activeScreen = _prList;
            _content.Add(_activeScreen);
            _prList.Load();
        }
        else
        {
            // No connection: show a hint instead of an empty pane.
            _activeScreen = new Label
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(),
                Text = "no Azure DevOps connection — run: cobalt auth login",
            };
            _content.Add(_activeScreen);
        }

        // Re-establish focus: disposing the previously-focused screen leaves focus
        // dangling, which stops Window.KeyDown from routing subsequent keys.
        _activeScreen.SetFocus();
    }

    /// <summary>
    /// Full chrome render: tab strip, keybar, status, message. Runs on construction,
    /// section/scope/context changes, and width changes — NOT per log message
    /// (Messages.Changed is wired to the lighter <see cref="RefreshMessage"/>).
    /// </summary>
    private void RefreshChrome()
    {
        _tabs.Text = TabStripFormatter.Sections(_vm.ActiveSection, _bindings);
        _lastChromeWidth = Viewport.Width;
        _keybar.Text = KeybarFormatter.Render(_bindings, ActiveScope, ChromeWidth);
        RefreshStatus();
        RefreshMessage();
        // Only the fixed-layout chrome labels changed text — mark them dirty and let the run loop
        // repaint them, instead of a whole-app LayoutAndDraw.
        _tabs.SetNeedsDraw();
        _keybar.SetNeedsDraw();
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
        }
        base.Dispose(disposing);
    }
}
