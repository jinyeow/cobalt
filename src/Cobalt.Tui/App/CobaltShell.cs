using Cobalt.Core.Config;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.Tasks;
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
    private readonly KeyBindingTable _bindings = KeyBindingTable.Default();
    private readonly KeymapRouter _router;

    private WorkItemListView? _workItemList;
    private PrListView? _prList;

    private readonly Label _tabs;
    private readonly View _content;
    private readonly Label _status;
    private readonly Label _message;
    private readonly Label _palettePrompt;
    private readonly TextField _palette;

    private View? _activeScreen;

    private readonly AdoContext? _context;

    public CobaltShell(
        IApplication app,
        ShellViewModel vm,
        WorkItemStoreAdapter? workItems = null,
        PullRequestStoreAdapter? pullRequests = null,
        EditorService? editor = null,
        AdoContext? context = null)
    {
        _app = app;
        _vm = vm;
        _workItems = workItems;
        _pullRequests = pullRequests;
        _context = context;
        _editor = editor ?? new EditorService(new ProcessEditorLauncher(
            Environment.GetEnvironmentVariable, TerminalGuiSuspender.For(app)));
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
        ShowSection();
        RefreshChrome();
    }

    public KeyScope ActiveScope => _vm.ActiveSection == AppSection.WorkItems
        ? KeyScope.WorkItemList
        : KeyScope.PullRequestList;

    private void WireViewModel()
    {
        _vm.SectionChanged += () => { ShowSection(); RefreshChrome(); };
        _vm.Messages.Changed += RefreshChrome;
        _vm.QuitRequested += _app.RequestStop;
        _vm.HelpRequested += ShowHelp;
        _vm.MessagesRequested += ShowMessages;
        _vm.PickContextRequested += () =>
            _vm.Messages.Info($"contexts: {string.Join(", ", _vm.ContextNames)} — switch with :ctx NAME");
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
        // In the PR section, Tab/S-Tab cycle the PR sub-tabs (review queue/mine/active)
        // rather than switching top-level sections; 1/2 still switch sections.
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
            // Force the frame now: without this the move only paints on the next event,
            // which reads as "the key needs a second press" on slower/Windows drivers.
            _app.LayoutAndDraw(false);
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
                RunWorkItemAction((a, store, id, ct) => a.RunCommentAsync(store, id, ct));
                break;
            case AppCommand.ChangeState:
                RunWorkItemAction((a, store, id, ct) => a.RunChangeStateAsync(store, id, ct));
                break;
            case AppCommand.Assign:
                RunWorkItemAction((a, store, id, ct) => a.RunAssignAsync(store, id, ct));
                break;
            case AppCommand.EditTags:
                RunWorkItemAction((a, store, id, ct) => a.RunTagsAsync(store, id, ct));
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

    private void RunWorkItemAction(Func<WorkItemActions, IWorkItemStore, long, CancellationToken, Task> run)
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
        var actions = new WorkItemActions(_app, _editor, _vm.Messages.Info);
        _ = RunThenRefreshAsync(run(actions, _workItems, id, CancellationToken.None), () => _workItemList?.OnRefresh());
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
            return AdoUrls.WorkItem(_context, wid);
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
        new PrDetailDialog(_app, detailVm, _editor, _vm.Messages.Info, _pullRequests).Show();
        _prList?.Load(); // reflect any votes/edits back into the list
    }

    private void OpenWorkItemDetail(long id)
    {
        if (_workItems is null)
        {
            return;
        }
        var detailVm = new WorkItemDetailViewModel(_workItems, id);
        new WorkItemDetailDialog(_app, detailVm, _editor, _vm.Messages.Info).Show();
        _workItemList?.OnRefresh(); // reflect any edits back into the list
    }

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

    private void RefreshChrome()
    {
        var wi = _vm.ActiveSection == AppSection.WorkItems ? "[g1:Work Items]" : " g1:Work Items ";
        var pr = _vm.ActiveSection == AppSection.PullRequests ? "[g2:Pull Requests]" : " g2:Pull Requests ";
        _tabs.Text = $" {wi} {pr}";
        _status.Text = _vm.StatusLine;
        var current = _vm.Messages.Current;
        _message.Text = current is null ? "" : $" {current.Text}";
        _app.LayoutAndDraw(false);
    }

    private void ShowHelp() => TextDialog.Show(_app, "keys", HelpText.For(_bindings, ActiveScope));

    private void ShowMessages()
    {
        var lines = _vm.Messages.History
            .Select(m => $"{m.At:HH:mm:ss} {(m.Level == MessageLevel.Error ? "E" : "I")} {m.Text}");
        TextDialog.Show(_app, "messages", string.Join("\n", lines));
    }
}
