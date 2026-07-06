using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
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
    private readonly WorkItemDetailViewModel _vm;
    private readonly EditorService _editor;
    private readonly Action<string> _log;
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

    public WorkItemDetailDialog(
        IApplication app, WorkItemDetailViewModel vm, EditorService editor, Action<string> log)
    {
        _app = app;
        _vm = vm;
        _editor = editor;
        _log = log;
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

    private void OnChanged() => _app.Invoke(() =>
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
        switch (token)
        {
            case "q" or "Esc":
                key.Handled = true;
                RequestClose();
                break;
            case "s":
                key.Handled = true;
                PickState();
                break;
            case "c":
                key.Handled = true;
                if (CommentAction is not null)
                {
                    CommentAction();
                }
                else
                {
                    _ = CommentAsync();
                }
                break;
            case "e":
                key.Handled = true;
                _ = EditDescriptionAsync();
                break;
            case "a":
                key.Handled = true;
                _ = PromptAndRun("assign to (unique name)", "", v => _vm.AssignAsync(v, Token));
                break;
            case "t":
                key.Handled = true;
                if (TagsAction is not null)
                {
                    TagsAction();
                }
                else
                {
                    _ = PromptAndRun("tags (semicolon separated)", string.Join("; ", _vm.Item?.Tags ?? []),
                        v => _vm.SetTagsAsync(v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Token));
                }
                break;
            default:
                break;
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

    private string RenderBody()
    {
        if (_vm.IsLoading)
        {
            return "loading…";
        }
        var item = _vm.Item;
        if (item is null)
        {
            return _vm.Error is { } e ? $"error: {e}" : "no data";
        }

        var lines = new List<string>
        {
            $"{item.WorkItemType} #{item.Id}   [{item.State}]",
            $"Title:    {item.Title}",
            $"Assigned: {item.AssignedToDisplayName ?? "(unassigned)"}",
            $"Iteration:{item.IterationPath}",
            $"Tags:     {string.Join(", ", item.Tags)}",
            $"Priority: {item.Priority?.ToString() ?? "-"}   Points: {item.StoryPoints?.ToString() ?? "-"}",
            "",
            "── Description ──" + (_vm.DescriptionLossy ? "  ⚠ rich HTML: editing may drop formatting" : ""),
            _vm.DescriptionMarkdown.Length == 0 ? "(empty)" : _vm.DescriptionMarkdown,
            "",
            $"── Comments ({_vm.Comments.Count}) ──",
        };
        lines.AddRange(_vm.Comments.Select(c => $"  {c.Author} ({c.CreatedDate:yyyy-MM-dd}): {c.TextMarkdown}"));
        if (_vm.Error is { } err)
        {
            lines.Add("");
            lines.Add($"error: {err}");
        }
        return string.Join('\n', lines);
    }

    private void PickState()
    {
        var states = _vm.AvailableStates.Select(s => s.Name).ToArray();
        if (states.Length == 0)
        {
            _log("no states available");
            return;
        }
        var choice = MessageBox.Query(_app, "change state", "", states);
        if (choice is { } index && index >= 0 && index < states.Length)
        {
            _ = RunAndLog(_vm.ChangeStateAsync(states[index], Token), $"state → {states[index]}");
        }
    }

    private async Task CommentAsync()
    {
        string? text;
        try
        {
            text = await _editor.EditAsync("", ".md", Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
        {
            _app.Invoke(() => _log($"editor failed: {ex.Message}"));
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            await RunAndLog(_vm.AddCommentAsync(text.Trim(), Token), "comment added").ConfigureAwait(false);
        }
    }

    private async Task EditDescriptionAsync()
    {
        string? edited;
        try
        {
            edited = await _editor.EditAsync(_vm.DescriptionMarkdown, ".md", Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
        {
            _app.Invoke(() => _log($"editor failed: {ex.Message}"));
            return;
        }
        if (edited is not null)
        {
            await RunAndLog(_vm.SaveDescriptionAsync(edited, Token), "description saved").ConfigureAwait(false);
        }
    }

    private async Task PromptAndRun(string title, string initial, Func<string, Task> action)
    {
        string? value;
        try
        {
            value = await _editor.EditAsync(initial, ".txt", Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
        {
            _app.Invoke(() => _log($"editor failed: {ex.Message}"));
            return;
        }
        if (value is not null)
        {
            await RunAndLog(action(value.Trim()), title).ConfigureAwait(false);
        }
    }

    private async Task RunAndLog(Task work, string success)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // dialog closed mid-op; nothing to report
        }
        _app.Invoke(() => _log(_vm.Error is { } e ? $"failed: {e}" : success));
    }
}
