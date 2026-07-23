using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.Tasks;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Modal work-item detail: renders fields, description (as Markdown), and the
/// comment thread; verb keys drive the mutations via <see cref="WorkItemDetailViewModel"/>.
/// </summary>
public sealed class WorkItemDetailDialog
{
    private readonly IApplication _app;
    // UI-thread marshalling seam for all pure Invoke marshalling (M2); Terminal.Gui (_app) is still
    // held for its non-marshalling uses — dialog construction, RequestStop, help/child dialogs.
    private readonly IUiPost _post;
    private readonly WorkItemDetailViewModel _vm;
    private readonly WorkItemActions _actions;
    private readonly Action<string> _log;
    private readonly KeyBindingTable _bindings;
    private readonly KeymapRouter _router;
    private readonly CancellationTokenSource _cts = new();
    private bool _closed;
    private Dialog? _dialog;
#pragma warning disable CS0618 // TextView superseded by external Editor package; read-only pane is fine
    private TextView? _body;

    private CancellationToken Token => _cts.Token;

    /// <summary>Test seam: the read-only scroll pane, exposed so a view-level test can drive scrolling.</summary>
    internal TextView Body => _body ?? throw new InvalidOperationException("Build() first");
#pragma warning restore CS0618

    /// <summary>Test seam: replaces the default close (app.RequestStop) so a test can observe close without a run loop.</summary>
    internal Action? CloseAction { get; set; }

    /// <summary>Test seam: replaces the real comment path (needs the editor) so a test can observe the 'c' key.</summary>
    internal Action? CommentAction { get; set; }

    /// <summary>Test seam: replaces the real tags path (needs the editor) so a test can observe the 't' key.</summary>
    internal Action? TagsAction { get; set; }

    /// <summary>Test seam: replaces the real help overlay (needs a run loop) so a test can observe '?'.</summary>
    internal Action? HelpAction { get; set; }

    public WorkItemDetailDialog(
        IApplication app, WorkItemDetailViewModel vm, EditorService editor, Action<string> log,
        ITextInput? textInput = null, KeyBindingTable? bindings = null, IUiPost? post = null)
    {
        _app = app;
        _post = post ?? new ApplicationUiPost(app);
        _vm = vm;
        _log = log;
        _actions = new WorkItemActions(app, editor, log, textInput);
        // The shell injects its (possibly remapped) table; a direct caller/test omits it and falls
        // back to the process-wide defaults. Help within the dialog renders from this same table.
        _bindings = bindings ?? KeyBindingTable.Shared;
        _router = new KeymapRouter(_bindings);
    }

    public void Show()
    {
        using var dialog = Build();
        _ = LoadAsync();
        _app.Run(dialog);

        // app.Run returned → dialog is closing; stop any late callbacks touching it.
        _closed = true;
        _vm.Changed -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Constructs and wires the dialog (body pane, verb keys, change subscription)
    /// without starting the load or run loop. Split out so view-level tests can
    /// drive key delivery headlessly.
    /// </summary>
    internal Dialog Build()
    {
        var dialog = new Dialog
        {
            Title = $"work item #{_vm.Id} — q close · s state · c comment · e edit · a assign · t tags",
            Width = Dim.Percent(90),
            Height = Dim.Percent(90),
        };
        _dialog = dialog;

#pragma warning disable CS0618 // TextView superseded by external Editor package; read-only pane is fine
        var body = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = true, // position indicator; content is scrolled pager-style (VimScroll)
        };
#pragma warning restore CS0618
        _body = body;

        _vm.Changed += OnChanged;

        // A focused ReadOnly TextView swallows every printable rune before the
        // dialog's own KeyDown runs, so subscribe the verb handler to the TextView
        // too. Verbs it matches stop propagation; keys it ignores (arrows/PageUp/
        // PageDown/Home/End) fall through to the TextView's native scrolling. The
        // dialog subscription stays as a safety net for when focus is elsewhere.
        body.KeyDown += HandleKey;
        dialog.KeyDown += HandleKey;

        dialog.Add(body);
        body.Text = RenderBody();
        return dialog;
    }

    private void OnChanged() => _post.Post(() =>
    {
        if (!_closed && _body is not null && _dialog is not null)
        {
            _body.Text = RenderBody();
            _dialog.SetNeedsDraw();
        }
    });

    private void HandleKey(object? sender, Terminal.Gui.Input.Key key)
    {
        var token = KeyTokenizer.ToToken(key);
        if (token is null)
        {
            return;
        }
        // Esc clears a pending count/sequence first; it only closes when nothing is
        // pending (mirrors the shell's Esc handling, L5).
        var hadPending = _router.HasPending;
        var result = _router.Feed(token, KeyScope.WorkItemDetail);
        switch (result.Kind)
        {
            case KeyResultKind.Pending:
                key.Handled = true; // swallow an in-progress sequence (e.g. after 'g')
                break;
            case KeyResultKind.Matched when Dispatch(result.Command, result.Count):
                key.Handled = true;
                break;
            case KeyResultKind.Matched:
                break; // matched but this dialog doesn't act — let native widget behavior run
            default:
                if (token == "Esc")
                {
                    key.Handled = true;
                    if (!hadPending)
                    {
                        RequestClose();
                    }
                }
                break;
        }
    }

    /// <summary>Runs the matched command; returns true when the dialog actually acted.</summary>
    private bool Dispatch(AppCommand command, int? count)
    {
        if (_body is not null && VimScroll.Applies(command))
        {
            VimScroll.Apply(_body, command, count);
            return true;
        }
        switch (command)
        {
            case AppCommand.Back:
                RequestClose();
                return true;
            case AppCommand.Help:
                if (HelpAction is not null)
                {
                    HelpAction();
                }
                else
                {
                    TextDialog.Show(_app, "keys", HelpText.ForDialog(_bindings, KeyScope.WorkItemDetail), _bindings);
                }
                return true;
            case AppCommand.ChangeState:
                _ = FireAndForget.Observe(_actions.ChangeStateAsync(_vm, Token), _post, _log);
                return true;
            case AppCommand.Comment:
                if (CommentAction is not null)
                {
                    CommentAction();
                }
                else
                {
                    _ = FireAndForget.Observe(_actions.CommentAsync(_vm, Token), _post, _log);
                }
                return true;
            case AppCommand.EditInEditor:
                _ = FireAndForget.Observe(_actions.EditDescriptionAsync(_vm, Token), _post, _log);
                return true;
            case AppCommand.Assign:
                _ = FireAndForget.Observe(_actions.AssignAsync(_vm, Token), _post, _log);
                return true;
            case AppCommand.EditTags:
                if (TagsAction is not null)
                {
                    TagsAction();
                }
                else
                {
                    _ = FireAndForget.Observe(_actions.TagsAsync(_vm, Token), _post, _log);
                }
                return true;
            default:
                return false;
        }
    }

    private async Task LoadAsync() => await _vm.LoadAsync(Token).IgnoreCancellationAsync();

    private void RequestClose()
    {
        _closed = true;
        if (CloseAction is not null)
        {
            CloseAction();
        }
        else if (_dialog is not null)
        {
            _app.RequestStop(_dialog);
        }
    }

    // Full tier is width-independent (the TextView word-wraps), so no width is threaded.
    private string RenderBody() => WorkItemDetailFormatter.Render(_vm, width: 0, PreviewTier.Full);
}
