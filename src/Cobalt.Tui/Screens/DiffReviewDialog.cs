using System.Collections.ObjectModel;
using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Modal diff review: a changed-file list (left) and the unified diff of the
/// selected file (right). `c` comments on the selected line, `x` opens threads.
/// </summary>
public sealed class DiffReviewDialog(
    IApplication app, PrDiffViewModel vm, EditorService editor, Action<string> log)
{
    private readonly CancellationTokenSource _cts = new();
    private bool _closed;
    private ListView _fileList = null!;
    private ListView _diffPane = null!;
    private Label _diffHeader = null!;

    private CancellationToken Token => _cts.Token;

    public void Show()
    {
        using var dialog = new Dialog
        {
            Title = $"diff review !{vm.PrId} — q close · Tab files/diff · c comment · [ ] next/prev file",
            Width = Dim.Percent(96),
            Height = Dim.Percent(96),
        };

        _fileList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(28),
            Height = Dim.Fill(),
            CanFocus = true,
        };
        _diffHeader = new Label { X = Pos.Right(_fileList) + 1, Y = 0, Width = Dim.Fill(), Height = 1 };
        _diffPane = new ListView
        {
            X = Pos.Right(_fileList) + 1,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
        };

        _fileList.Accepting += (_, _) => _ = SelectFile(_fileList.SelectedItem ?? 0);

        void OnChanged() => app.Invoke(() =>
        {
            if (!_closed)
            {
                Render();
            }
        });
        vm.Changed += OnChanged;

        dialog.KeyDown += (_, key) =>
        {
            var token = KeyTokenizer.ToToken(key);
            switch (token)
            {
                case "q" or "Esc":
                    key.Handled = true;
                    _closed = true;
                    app.RequestStop(dialog);
                    break;
                case "Tab":
                    key.Handled = true;
                    if (_diffPane.HasFocus)
                    {
                        _fileList.SetFocus();
                    }
                    else
                    {
                        _diffPane.SetFocus();
                    }
                    break;
                case "]":
                    key.Handled = true;
                    _ = SelectFile((_fileList.SelectedItem ?? 0) + 1);
                    break;
                case "[":
                    key.Handled = true;
                    _ = SelectFile((_fileList.SelectedItem ?? 0) - 1);
                    break;
                case "c":
                    key.Handled = true;
                    _ = CommentAsync();
                    break;
                default:
                    break;
            }
        };

        dialog.Add(_fileList, _diffHeader, _diffPane);
        _ = LoadAsync();
        app.Run(dialog);

        _closed = true;
        vm.Changed -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task LoadAsync()
    {
        try
        {
            await vm.LoadAsync(Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SelectFile(int index)
    {
        if (vm.Files.Count == 0)
        {
            return;
        }
        try
        {
            await vm.SelectFileAsync(index, Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CommentAsync()
    {
        if (vm.CurrentDiff is null || vm.CurrentDiff.Lines.Count == 0)
        {
            return;
        }
        var lineIndex = _diffPane.SelectedItem ?? 0;
        var text = await editor.EditAsync("", ".md", Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        await vm.AddCommentAtLineAsync(lineIndex, text.Trim(), Token).ConfigureAwait(false);
        app.Invoke(() => log(vm.Error is { } e ? $"comment failed: {e}" : "line comment added"));
    }

    private void Render()
    {
        if (vm.IsLoading)
        {
            _diffHeader.Text = " loading diff…";
        }
        else if (vm.Error is { } e)
        {
            _diffHeader.Text = $" error: {e}";
        }

        var files = new ObservableCollection<string>(vm.Files.Select(FormatFile));
        _fileList.SetSource(files);

        if (vm.CurrentDiff is { } diff)
        {
            var file = vm.SelectedFile;
            _diffHeader.Text = file is null
                ? ""
                : $" {file.Path}   +{diff.Additions} -{diff.Deletions}" +
                  (diff.IsBinary ? "  (binary)" : diff.TooLarge ? "  (too large)" : "");
            _diffPane.SetSource(new ObservableCollection<string>(diff.Lines.Select(l => FormatLine(diff, l))));
        }
        _diffPane.SetNeedsDraw();
        _fileList.SetNeedsDraw();
    }

    private static string FormatFile(FileChange f)
    {
        var glyph = f.ChangeType switch
        {
            FileChangeKind.Add => "+",
            FileChangeKind.Delete => "-",
            FileChangeKind.Rename => "»",
            _ => "~",
        };
        var name = f.Path.Length <= 34 ? f.Path : "…" + f.Path[^33..];
        return $"{glyph} {name}";
    }

    private string FormatLine(FileDiff diff, DiffLine line)
    {
        var marker = vm.ThreadsForCurrentFileLine(line.NewLineNumber ?? -1).Count > 0 ? "●" : " ";
        var sign = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "-",
            _ => " ",
        };
        var oldNo = line.OldLineNumber?.ToString() ?? "";
        var newNo = line.NewLineNumber?.ToString() ?? "";
        return $"{marker}{oldNo,4} {newNo,4} {sign}{line.Text}";
    }
}
