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
    private int _fileCount = -1;
    private int _fileIndex;
    private string? _renderedDiffPath;

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

        // Enter on the file list opens the highlighted file (SelectedItem is valid at that moment).
        _fileList.Accepting += (_, e) =>
        {
            e.Handled = true;
            _ = SelectFile(_fileList.SelectedItem ?? _fileIndex);
        };

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
                    _ = SelectFile(_fileIndex + 1);
                    break;
                case "[":
                    key.Handled = true;
                    _ = SelectFile(_fileIndex - 1);
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
        _fileIndex = Math.Clamp(index, 0, vm.Files.Count - 1);
        try
        {
            await vm.SelectFileAsync(_fileIndex, Token).ConfigureAwait(false);
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
        string? text;
        try
        {
            text = await editor.EditAsync("", ".md", Token).ConfigureAwait(false);
        }
        catch (EditorLaunchException ex)
        {
            app.Invoke(() => log($"editor failed: {ex.Message}"));
            return;
        }
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

        // Rebuild the file list only when the set changes; SetSource nulls the
        // selection, so restore it afterwards to keep the highlight and nav index.
        if (vm.Files.Count != _fileCount)
        {
            _fileCount = vm.Files.Count;
            _fileList.SetSource(new ObservableCollection<string>(vm.Files.Select(FormatFile)));
        }
        if (vm.Files.Count > 0)
        {
            _fileList.SelectedItem = Math.Clamp(_fileIndex, 0, vm.Files.Count - 1);
        }

        if (vm.CurrentDiff is { } diff)
        {
            var file = vm.SelectedFile;
            _diffHeader.Text = file is null
                ? ""
                : $" {file.Path}   +{diff.Additions} -{diff.Deletions}" +
                  (diff.IsBinary ? "  (binary)" : diff.TooLarge ? "  (too large)" : "");

            // Rebuild the diff pane (thread markers may have changed after a comment),
            // but preserve the reviewer's line position on a same-file refresh; reset
            // to the top only when the selected file actually changed.
            var sameFile = file?.Path == _renderedDiffPath;
            var keepLine = sameFile ? _diffPane.SelectedItem : 0;
            _renderedDiffPath = file?.Path;
            _diffPane.SetSource(new ObservableCollection<string>(diff.Lines.Select(FormatLine)));
            if (diff.Lines.Count > 0)
            {
                _diffPane.SelectedItem = Math.Clamp(keepLine ?? 0, 0, diff.Lines.Count - 1);
            }
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

    private string FormatLine(DiffLine line)
    {
        var marker = vm.ThreadsForDiffLine(line).Count > 0 ? "●" : " ";
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
