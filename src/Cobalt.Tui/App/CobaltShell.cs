using Cobalt.Tui.Input;
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
    private readonly KeyBindingTable _bindings = KeyBindingTable.Default();
    private readonly KeymapRouter _router;

    private readonly Label _tabs;
    private readonly View _content;
    private readonly Label _status;
    private readonly Label _message;
    private readonly Label _palettePrompt;
    private readonly TextField _palette;

    private View? _activeScreen;

    public CobaltShell(IApplication app, ShellViewModel vm)
    {
        _app = app;
        _vm = vm;
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
            case AppCommand.FilterStart:
            case AppCommand.Open:
                _vm.Messages.Info("data screens arrive with M3/M4");
                break;
            default:
                break;
        }
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
        _content.SetFocus();
    }

    private void ShowSection()
    {
        if (_activeScreen is not null)
        {
            _content.Remove(_activeScreen);
            _activeScreen.Dispose();
        }

        // M2 placeholder screens; M3/M4 swap in the real list views.
        _activeScreen = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(),
            Text = _vm.ActiveSection == AppSection.WorkItems
                ? "Work items land in M3.  ?:help  ::palette  1/2:sections"
                : "Pull requests land in M4.  ?:help  ::palette  1/2:sections",
        };
        _content.Add(_activeScreen);
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
