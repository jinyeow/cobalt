using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
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
        using var dialog = new Dialog
        {
            Title = $"work item #{_vm.Id} — q close · s state · c comment · e edit · a assign · t tags",
            Width = Dim.Percent(90),
            Height = Dim.Percent(90),
        };

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

        _vm.Changed += () => _app.Invoke(() => { body.Text = RenderBody(); dialog.SetNeedsDraw(); });

        dialog.KeyDown += (_, key) =>
        {
            var token = KeyTokenizer.ToToken(key);
            switch (token)
            {
                case "q" or "Esc":
                    key.Handled = true;
                    _app.RequestStop(dialog);
                    break;
                case "s":
                    key.Handled = true;
                    PickState();
                    break;
                case "c":
                    key.Handled = true;
                    _ = CommentAsync();
                    break;
                case "e":
                    key.Handled = true;
                    _ = EditDescriptionAsync();
                    break;
                case "a":
                    key.Handled = true;
                    _ = PromptAndRun("assign to (unique name)", "", v => _vm.AssignAsync(v, CancellationToken.None));
                    break;
                case "t":
                    key.Handled = true;
                    _ = PromptAndRun("tags (semicolon separated)", string.Join("; ", _vm.Item?.Tags ?? []),
                        v => _vm.SetTagsAsync(v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), CancellationToken.None));
                    break;
                default:
                    break;
            }
        };

        dialog.Add(body);
        _ = _vm.LoadAsync(CancellationToken.None);
        body.Text = RenderBody();
        _app.Run(dialog);
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
            _ = RunAndLog(_vm.ChangeStateAsync(states[index], CancellationToken.None), $"state → {states[index]}");
        }
    }

    private async Task CommentAsync()
    {
        var text = await _editor.EditAsync("", ".md", CancellationToken.None).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await RunAndLog(_vm.AddCommentAsync(text.Trim(), CancellationToken.None), "comment added").ConfigureAwait(false);
        }
    }

    private async Task EditDescriptionAsync()
    {
        var edited = await _editor.EditAsync(_vm.DescriptionMarkdown, ".md", CancellationToken.None).ConfigureAwait(false);
        if (edited is not null)
        {
            await RunAndLog(_vm.SaveDescriptionAsync(edited, CancellationToken.None), "description saved").ConfigureAwait(false);
        }
    }

    private async Task PromptAndRun(string title, string initial, Func<string, Task> action)
    {
        var value = await _editor.EditAsync(initial, ".txt", CancellationToken.None).ConfigureAwait(false);
        if (value is not null)
        {
            await RunAndLog(action(value.Trim()), title).ConfigureAwait(false);
        }
    }

    private async Task RunAndLog(Task work, string success)
    {
        await work.ConfigureAwait(false);
        _app.Invoke(() => _log(_vm.Error is { } e ? $"failed: {e}" : success));
    }
}
