using System.Collections.ObjectModel;
using Cobalt.Core.Models;
using Cobalt.Core.Text;
using Cobalt.Core.Text.Syntax;
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
/// Modal diff review: a changed-file list (left) and the unified diff of the
/// selected file (right). `c` comments on the selected line, `[`/`]` change file,
/// Tab cycles the panes, and j/k/gg/G/Ctrl-d/u scroll the focused pane.
/// </summary>
public sealed class DiffReviewDialog(
    IApplication app, PrDiffViewModel vm, EditorService editor, Action<string> log)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly KeymapRouter _router = new(KeyBindingTable.Default());
    private bool _closed;
    private Dialog? _dialog;
    private ListView _fileList = null!;
    private ListView _diffPane = null!;
    private Label _diffHeader = null!;
    private readonly HashSet<string> _collapsedDirs = new(StringComparer.Ordinal);
    private IReadOnlyList<FileTreeRow> _rows = [];
    private List<string> _fileListStrings = [];
    private int _fileIndex;
    private string? _renderedDiffPath;

    private CancellationToken Token => _cts.Token;

    /// <summary>Test seam: replaces the default close (app.RequestStop) so a test can observe close without a run loop.</summary>
    internal Action? CloseAction { get; set; }

    /// <summary>Test seam: replaces the real help overlay (needs a run loop) so a test can observe '?'.</summary>
    internal Action? HelpAction { get; set; }

    /// <summary>Test seam: the changed-file list pane.</summary>
    internal ListView FileList => _fileList;

    /// <summary>Test seam: the unified-diff pane.</summary>
    internal ListView DiffPane => _diffPane;

    /// <summary>Test seam: the currently selected file index.</summary>
    internal int FileIndex => _fileIndex;

    /// <summary>Test seam: the flattened file-tree rows currently shown in the file list.</summary>
    internal IReadOnlyList<FileTreeRow> Rows => _rows;

    public void Show()
    {
        using var dialog = Build();
        _ = LoadAsync();
        app.Run(dialog);

        _closed = true;
        vm.Changed -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Constructs and wires the dialog (file list, diff pane, verb keys, change
    /// subscription) without starting the load or run loop. Split out so view-level
    /// tests can drive key delivery headlessly.
    /// </summary>
    internal Dialog Build()
    {
        var dialog = new Dialog
        {
            Title = $"diff review !{vm.PrId} — q close · Tab files/diff · [ ] file · z fold · c comment · ? keys",
            Width = Dim.Percent(96),
            Height = Dim.Percent(96),
        };
        _dialog = dialog;

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
            // The diff pane is a code view: use the code-oriented Base scheme so
            // the theme's distinct VisualRole.Code* token foregrounds resolve
            // (the Dialog scheme collapses them all to its Normal foreground).
            SchemeName = "Base",
        };

        // Vim command keys (q, c, j, k, …) drive this dialog, so disable ListView
        // type-ahead search — otherwise the CollectionNavigator swallows those
        // letters before they reach the dialog's key handler. Tradeoff: no
        // type-a-filename-to-jump in the file list; j/k and [ ] cover navigation.
        _fileList.KeystrokeNavigator = null;
        _diffPane.KeystrokeNavigator = null;

        // Enter on the file list opens the highlighted file, or toggles a folder row.
        _fileList.Accepting += (_, e) =>
        {
            e.Handled = true;
            ActivateRow(_fileList.SelectedItem ?? -1);
        };

        vm.Changed += OnChanged;
        dialog.KeyDown += HandleKey;

        dialog.Add(_fileList, _diffHeader, _diffPane);
        Render();
        return dialog;
    }

    private void OnChanged() => app.Invoke(() =>
    {
        if (!_closed)
        {
            Render();
        }
    });

    private void HandleKey(object? sender, Terminal.Gui.Input.Key key)
    {
        var token = KeyTokenizer.ToToken(key);
        if (token is null)
        {
            return;
        }
        // Esc's job is to clear a pending count/sequence first; only when nothing is
        // pending does it close the dialog (mirrors the shell's Esc handling, L5).
        var hadPending = _router.HasPending;
        var result = _router.Feed(token, KeyScope.DiffReview);
        switch (result.Kind)
        {
            case KeyResultKind.Pending:
                key.Handled = true; // swallow an in-progress sequence (e.g. after 'g')
                break;
            case KeyResultKind.Matched when Dispatch(result.Command, result.Count):
                key.Handled = true;
                break;
            case KeyResultKind.Matched:
                break; // matched but unhandled — let native behavior run (e.g. Enter → file Accepting)
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
        if (VimScroll.Applies(command))
        {
            // Scroll whichever pane has focus (file list or diff).
            VimScroll.Apply(_diffPane.HasFocus ? _diffPane : _fileList, command, count);
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
                    TextDialog.Show(app, "keys", HelpText.ForDialog(_router.Table, KeyScope.DiffReview));
                }
                return true;
            case AppCommand.CyclePane:
                if (_diffPane.HasFocus)
                {
                    _fileList.SetFocus();
                }
                else
                {
                    _diffPane.SetFocus();
                }
                return true;
            case AppCommand.NextFile:
                _ = StepFile(count ?? 1);
                return true;
            case AppCommand.PrevFile:
                _ = StepFile(-(count ?? 1));
                return true;
            case AppCommand.ToggleFold:
                ToggleFoldAtCursor();
                return true;
            case AppCommand.Comment:
                _ = FireAndForget.Observe(CommentAsync(), app, log);
                return true;
            default:
                return false;
        }
    }

    private void RequestClose()
    {
        _closed = true;
        if (CloseAction is not null)
        {
            CloseAction();
        }
        else if (_dialog is not null)
        {
            app.RequestStop(_dialog);
        }
    }

    private async Task LoadAsync() => await vm.LoadAsync(Token).IgnoreCancellationAsync();

    private async Task SelectFile(int index)
    {
        if (vm.Files.Count == 0)
        {
            return;
        }
        _fileIndex = Math.Clamp(index, 0, vm.Files.Count - 1);
        await vm.SelectFileAsync(_fileIndex, Token).IgnoreCancellationAsync();
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
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
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

        // Rebuild the file tree, keeping the highlight on the displayed file's row.
        RebuildFileList(SelectedFileNodePath());

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

            var language = LanguageDetector.FromPath(file?.Path ?? "");
            var styled = diff.Lines
                .Select(l => DiffLineStyler.Compose(
                    l,
                    SyntaxTokenizer.Tokenize(l.Text, language),
                    vm.ThreadsForDiffLine(l).Count > 0))
                .ToList();
            _diffPane.Source = new DiffListDataSource(styled);
            if (diff.Lines.Count > 0)
            {
                _diffPane.SelectedItem = Math.Clamp(keepLine ?? 0, 0, diff.Lines.Count - 1);
            }
        }
        _diffPane.SetNeedsDraw();
        _fileList.SetNeedsDraw();
    }

    /// <summary>
    /// Re-flattens the changed files into the directory tree, refreshes the list
    /// source only when the rendered rows actually change (SetSource nulls the
    /// selection), and restores the highlight to <paramref name="selectNodePath"/>.
    /// </summary>
    private void RebuildFileList(string? selectNodePath)
    {
        _rows = FileTree.Flatten(vm.Files, _collapsedDirs);
        var strings = _rows.Select(FormatRow).ToList();
        if (!strings.SequenceEqual(_fileListStrings, StringComparer.Ordinal))
        {
            _fileListStrings = strings;
            _fileList.SetSource(new ObservableCollection<string>(strings));
        }
        if (_rows.Count == 0)
        {
            return;
        }
        var target = selectNodePath is null ? -1 : IndexOfNode(selectNodePath);
        _fileList.SelectedItem = target >= 0 ? target : Math.Clamp(_fileList.SelectedItem ?? 0, 0, _rows.Count - 1);
    }

    /// <summary>Enter/click on a row: open the file, or toggle a folder's collapsed state.</summary>
    private void ActivateRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            return;
        }
        var row = _rows[rowIndex];
        if (row.Kind == FileTreeRowKind.Directory)
        {
            ToggleFold(row.NodePath);
        }
        else if (row.FileIndex is { } fileIndex)
        {
            _ = SelectFile(fileIndex);
        }
    }

    /// <summary>z: collapse/expand the folder under the cursor, or the nearest ancestor folder of a file row.</summary>
    private void ToggleFoldAtCursor()
    {
        var sel = _fileList.SelectedItem ?? -1;
        if (sel < 0 || sel >= _rows.Count)
        {
            return;
        }
        var row = _rows[sel];
        if (row.Kind == FileTreeRowKind.Directory)
        {
            ToggleFold(row.NodePath);
            return;
        }
        var parent = NearestAncestorDir(sel);
        if (parent is not null)
        {
            _collapsedDirs.Add(parent.NodePath);
            RebuildFileList(parent.NodePath);
            _fileList.SetNeedsDraw();
        }
    }

    private void ToggleFold(string nodePath)
    {
        if (!_collapsedDirs.Remove(nodePath))
        {
            _collapsedDirs.Add(nodePath);
        }
        RebuildFileList(nodePath);
        _fileList.SetNeedsDraw();
    }

    /// <summary>[ / ]: move to the previous/next file among the visible leaves, skipping folder rows.</summary>
    private async Task StepFile(int delta)
    {
        var fileRows = _rows.Where(r => r.FileIndex is not null).ToList();
        if (fileRows.Count == 0)
        {
            return;
        }
        var current = fileRows.FindIndex(r => r.FileIndex == _fileIndex);
        var next = Math.Clamp((current < 0 ? 0 : current) + delta, 0, fileRows.Count - 1);
        await SelectFile(fileRows[next].FileIndex!.Value);
    }

    private FileTreeRow? NearestAncestorDir(int rowIndex)
    {
        var depth = _rows[rowIndex].Depth;
        for (var i = rowIndex - 1; i >= 0; i--)
        {
            if (_rows[i].Kind == FileTreeRowKind.Directory && _rows[i].Depth < depth)
            {
                return _rows[i];
            }
        }
        return null;
    }

    private int IndexOfNode(string nodePath)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].NodePath, nodePath, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The tree node path of the file currently shown in the diff pane, for highlight restore.</summary>
    private string? SelectedFileNodePath() =>
        _fileIndex >= 0 && _fileIndex < vm.Files.Count ? vm.Files[_fileIndex].Path : null;

    private static string FormatRow(FileTreeRow row)
    {
        var indent = new string(' ', row.Depth * 2);
        if (row.Kind == FileTreeRowKind.Directory)
        {
            return $"{indent}{(row.Collapsed ? "▸" : "▾")} {row.Label}/";
        }
        var glyph = row.ChangeType switch
        {
            FileChangeKind.Add => "+",
            FileChangeKind.Delete => "-",
            FileChangeKind.Rename => "»",
            _ => "~",
        };
        return $"{indent}{glyph} {row.Label}";
    }
}
