using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
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
    private readonly EditorService _editor;
    private readonly KeyBindingTable _bindings = KeyBindingTable.Default();
    private readonly KeymapRouter _router;

    private WorkItemListView? _workItemList;

    private readonly Label _tabs;
    private readonly View _content;
    private readonly Label _status;
    private readonly Label _message;
    private readonly Label _palettePrompt;
    private readonly TextField _palette;

    private View? _activeScreen;

    public CobaltShell(
        IApplication app,
        ShellViewModel vm,
        WorkItemStoreAdapter? workItems = null,
        EditorService? editor = null)
    {
        _app = app;
        _vm = vm;
        _workItems = workItems;
        // TODO(M6): drive Terminal.Gui's terminal suspend/resume around the editor
        // process so a full-screen $EDITOR doesn't fight the driver for the screen.
        _editor = editor ?? new EditorService(new ProcessEditorLauncher(Environment.GetEnvironmentVariable));
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
            if (result.Kind == KeyResultKind.Pending)
            {
                key.Handled = true;
                return;
            }
            if (result.Kind != KeyResultKind.Matched)
            {
                return;
            }

            key.Handled = true;
            Dispatch(result.Command);
        };
    }

    private void Dispatch(AppCommand command)
    {
        if (_vm.HandleCommand(command))
        {
            return;
        }

        switch (command)
        {
            case AppCommand.CommandPalette:
                OpenPalette();
                break;
            case AppCommand.Back:
                _vm.Messages.Info("nothing to close — quit with :q");
                break;
            case AppCommand.Refresh:
                _workItemList?.OnRefresh();
                break;
            case AppCommand.FilterStart:
                _workItemList?.StartFiltering();
                break;
            case AppCommand.Open:
                _workItemList?.OnOpen();
                break;
            default:
                break;
        }
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
        }

        if (_vm.ActiveSection == AppSection.WorkItems && _workItems is not null)
        {
            var listVm = new WorkItemListViewModel(_workItems);
            _workItemList = new WorkItemListView(_app, listVm);
            _workItemList.ItemActivated += OpenWorkItemDetail;
            _activeScreen = _workItemList;
            _content.Add(_activeScreen);
            _workItemList.Load();
        }
        else
        {
            // Pull requests land in M4; work items with no connection show a hint.
            _activeScreen = new Label
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(),
                Text = _vm.ActiveSection == AppSection.WorkItems
                    ? "no Azure DevOps connection — run: cobalt auth login"
                    : "Pull requests land in M4.  ?:help  ::palette  1/2:sections",
            };
            _content.Add(_activeScreen);
        }

        // Re-establish focus: disposing the previously-focused screen leaves focus
        // dangling, which stops Window.KeyDown from routing subsequent keys.
        _activeScreen.SetFocus();
    }

    private void RefreshChrome()
    {
        var wi = _vm.ActiveSection == AppSection.WorkItems ? "[1:Work Items]" : " 1:Work Items ";
        var pr = _vm.ActiveSection == AppSection.PullRequests ? "[2:Pull Requests]" : " 2:Pull Requests ";
        _tabs.Text = $" {wi} {pr}";
        _status.Text = _vm.StatusLine;
        var current = _vm.Messages.Current;
        _message.Text = current is null ? "" : $" {current.Text}";
        _app.LayoutAndDraw(false);
    }

    private void ShowHelp() => ShowTextDialog("keys", HelpText.For(_bindings, ActiveScope));

    private void ShowMessages()
    {
        var lines = _vm.Messages.History
            .Select(m => $"{m.At:HH:mm:ss} {(m.Level == MessageLevel.Error ? "E" : "I")} {m.Text}");
        ShowTextDialog("messages", string.Join("\n", lines));
    }

    /// <summary>Scrollable modal text (MessageBox chokes on content taller than the screen).</summary>
    private void ShowTextDialog(string title, string text)
    {
        using var dialog = new Dialog
        {
            Title = $"{title} — q to close",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };
        // TextView is marked obsolete in favor of the external tui-cs/Editor package;
        // a read-only scrollable pane doesn't justify that dependency.
#pragma warning disable CS0618
        var view = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = text,
        };
#pragma warning restore CS0618
        dialog.KeyDown += (_, key) =>
        {
            var token = KeyTokenizer.ToToken(key);
            if (token is "q" or "Esc" or "Enter")
            {
                key.Handled = true;
                _app.RequestStop(dialog);
            }
        };
        dialog.Add(view);
        _app.Run(dialog);
    }
}
