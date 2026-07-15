using System.Collections.ObjectModel;
using Cobalt.Core.Config;
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
    IApplication app, PrDiffViewModel vm, ITextInput textInput, Action<string> log, AdoContext? context = null)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly KeymapRouter _router = new(KeyBindingTable.Default());
    private bool _closed;
    private Dialog? _dialog;
    private ListView _fileList = null!;
    private ListView _diffPane = null!;
    private Label _diffHeader = null!;
    private TextField _searchBar = null!;
    private readonly HashSet<string> _collapsedDirs = new(StringComparer.Ordinal);
    private IReadOnlyList<FileTreeRow> _rows = [];
    private List<string> _fileListStrings = [];
    private int _fileIndex;
    private string? _renderedDiffPath;
    private bool _sideBySide;
    private DiffFoldState? _foldState;
    private IReadOnlyList<DiffRow> _diffRows = [];
    private string? _searchQuery;
    private IReadOnlyList<(int LineIndex, LineSpan Span)> _searchMatches = [];
    private int _searchIndex;
    private int _lastDialogWidth = -1;
    private int _diffContentWidth = 1;
    private readonly DiffStyleCache _styleCache = new();
    private readonly CoalescingGate _statsRefresh = new();

    private CancellationToken Token => _cts.Token;

    private static readonly string[] VoteLabels =
        ["approve", "approve w/ suggestions", "wait for author", "reject", "reset"];

    private static readonly PrVote[] VoteValues =
        [PrVote.Approved, PrVote.ApprovedWithSuggestions, PrVote.WaitingForAuthor, PrVote.Rejected, PrVote.NoVote];

    /// <summary>Test seam: replaces the default close (app.RequestStop) so a test can observe close without a run loop.</summary>
    internal Action? CloseAction { get; set; }

    /// <summary>Test seam: replaces the real help overlay (needs a run loop) so a test can observe '?'.</summary>
    internal Action? HelpAction { get; set; }

    /// <summary>Test seam: replaces the real comment overlay (needs a run loop) so a test can observe the shown text.</summary>
    internal Action<string>? ViewThreadAction { get; set; }

    /// <summary>Test seam: supplies the search query in place of the editor prompt (needs a run loop).</summary>
    internal Func<string?>? SearchPromptAction { get; set; }

    /// <summary>Test seam: opens the branch/PR URL in place of the OS browser launch (needs a real process).</summary>
    internal Action<string>? OpenUrlAction { get; set; }

    /// <summary>Test seam: replaces the real vote picker (needs MessageBox/run loop) so a test can drive the choice.</summary>
    internal Func<string, IReadOnlyList<string>, int?>? VoteChooser { get; set; }

    /// <summary>Test seam: replaces applying the chosen vote (needs app.Invoke/run loop) so a test can observe it.</summary>
    internal Action<PrVote>? VoteAction { get; set; }

    /// <summary>Test seam: the number of matches for the active search (0 when no search).</summary>
    internal int SearchMatchCount => _searchMatches.Count;

    /// <summary>Test seam: the changed-file list pane.</summary>
    internal ListView FileList => _fileList;

    /// <summary>Test seam: the unified-diff pane.</summary>
    internal ListView DiffPane => _diffPane;

    /// <summary>Test seam: the header above the diff pane (file path and stats).</summary>
    internal Label DiffHeader => _diffHeader;

    /// <summary>Test seam: the inline search bar (hidden until '/').</summary>
    internal TextField SearchBar => _searchBar;

    /// <summary>Test seam: the currently selected file index.</summary>
    internal int FileIndex => _fileIndex;

    /// <summary>Test seam: the flattened file-tree rows currently shown in the file list.</summary>
    internal IReadOnlyList<FileTreeRow> Rows => _rows;

    /// <summary>Test seam: whether the diff pane is in side-by-side mode.</summary>
    internal bool SideBySide => _sideBySide;

    /// <summary>Test seam: the side-by-side row map, projected from the unified row map (empty in unified mode).</summary>
    internal IReadOnlyList<SideBySideRow> SideBySideRows =>
        _sideBySide ? [.. _diffRows.Select(r => new SideBySideRow(r.LeftIndex, r.RightIndex))] : [];

    /// <summary>Test seam: the single row→line map every diff-pane consumer (comment, thread, nav, search) goes through.</summary>
    internal IReadOnlyList<DiffRow> DiffRows => _diffRows;

    /// <summary>Test seam: the unified diff-line index the comment action would target for the current selection.</summary>
    internal int SelectedDiffLineIndex => SelectedDiffLine();

    /// <summary>Test seam: whether the changed-file list pane is currently shown (hidden at narrow widths).</summary>
    internal bool FileListVisible => _fileList.Visible;

    public void Show()
    {
        using var dialog = Build();
        _ = LoadAsync();
        try
        {
            app.Run(dialog);
        }
        finally
        {
            _closed = true;
            vm.Changed -= OnChanged;
            vm.StatsChanged -= OnStatsChanged;
            dialog.ViewportChanged -= OnViewportChanged;
            _cts.Cancel();
            _cts.Dispose();
        }
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
            Title = TitleFor(),
            Width = Dim.Percent(96),
            Height = Dim.Percent(96),
        };
        _dialog = dialog;

        // Pane X/Width are set by ApplyResponsiveLayout (called from Render) so they can
        // collapse gracefully at narrow widths; the initial values are placeholders.
        _fileList = new ListView
        {
            X = 0,
            Y = 0,
            Width = 28,
            Height = Dim.Fill(),
            CanFocus = true,
        };
        _diffHeader = new Label { X = 29, Y = 0, Width = Dim.Fill(), Height = 1 };
        _diffPane = new ListView
        {
            X = 29,
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

        // Enter on the diff pane opens the selected line's comment thread(s). The Dialog
        // consumes Enter as a default-accept before Dispatch's Open case can run, so mirror
        // the file-list workaround: handle it here and stop the accept from closing the dialog.
        _diffPane.Accepting += (_, e) =>
        {
            e.Handled = true;
            ViewThreadAtCursor();
        };

        // A one-line inline search bar anchored to the bottom, hidden until '/'. Enter applies
        // the query and hides it; Esc hides and clears. While it has focus HandleKey early-returns
        // so typed runes reach the field rather than the command router; if focus leaves the bar
        // any other way (Tab, a mouse click), HasFocusChanged hides it so it never orphans with
        // stale text (which would otherwise make the next q/Esc close the whole dialog).
        _searchBar = new TextField
        {
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Visible = false,
        };
        _searchBar.Accepting += (_, e) =>
        {
            e.Handled = true; // stop the Dialog's default-accept from closing us
            ApplySearch(_searchBar.Text);
            HideSearchBar();
        };
        _searchBar.KeyDown += (_, k) =>
        {
            if (k == Terminal.Gui.Input.Key.Esc)
            {
                k.Handled = true;
                HideSearchBar();
                ApplySearch(null); // clear the highlight/matches
            }
        };
        // Focus escaping the bar without Enter/Esc (Tab, a click) must not leave it orphaned;
        // hide it (cancel). Idempotent with HideSearchBar, which sets Visible=false first.
        _searchBar.HasFocusChanged += (_, _) =>
        {
            if (!_searchBar.HasFocus && _searchBar.Visible)
            {
                _searchBar.Visible = false;
                _searchBar.SetNeedsDraw();
                _dialog?.SetNeedsDraw();
            }
        };

        vm.Changed += OnChanged;
        vm.StatsChanged += OnStatsChanged;
        dialog.KeyDown += HandleKey;
        // Re-apply the responsive layout when the terminal (and so the dialog) is resized.
        dialog.ViewportChanged += OnViewportChanged;

        dialog.Add(_fileList, _diffHeader, _diffPane, _searchBar);
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

    // Background stats prefetch: only the title totals and file-row stats changed, so refresh the
    // chrome and skip the diff-pane rebuild — the displayed file's diff is unchanged. The prefetch
    // raises this once per file and each refresh rebuilds the whole file tree, so a burst collapses
    // into one queued refresh rather than one per file competing with the reviewer's keys.
    private void OnStatsChanged()
    {
        if (!_statsRefresh.TryQueue())
        {
            return; // a refresh is already queued; it will pick this file's stats up too
        }
        app.Invoke(() =>
        {
            // Reopened before rendering, so stats arriving during the render queue a fresh refresh.
            _statsRefresh.Release();
            if (!_closed)
            {
                Render(includeDiffPane: false);
            }
        });
    }

    private void OnViewportChanged(object? sender, Terminal.Gui.ViewBase.DrawEventArgs e)
    {
        if (_closed || _dialog is null || _dialog.Viewport.Width == _lastDialogWidth)
        {
            return;
        }
        _lastDialogWidth = _dialog.Viewport.Width;
        Render();
    }

    /// <summary>
    /// Collapse the panes gracefully at narrow widths: hide the file list below a
    /// threshold (diff takes the whole row), size it as a fraction otherwise, and force
    /// unified when the diff pane is too narrow for two columns. Pure decision in
    /// <see cref="ResponsiveLayout"/>; this only applies it to the views.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        var total = _dialog?.Viewport.Width ?? 0;
        var layout = ResponsiveLayout.Compute(total);

        _fileList.Visible = layout.ShowFileList;
        _fileList.Width = Math.Max(1, layout.FileListWidth);
        var diffX = layout.ShowFileList ? layout.FileListWidth + 1 : 0;
        _diffHeader.X = diffX;
        _diffPane.X = diffX;
        _diffContentWidth = Math.Max(1, total - diffX);

        if (!layout.AllowSideBySide)
        {
            _sideBySide = false; // too narrow for two columns — stay unified
        }
    }

    private void HandleKey(object? sender, Terminal.Gui.Input.Key key)
    {
        // While the inline search bar is focused, let its own handlers (Enter/Esc) and the
        // TextField (runes, '/', Tab) own the key — the command router must not intercept it.
        if (_searchBar.Visible && _searchBar.HasFocus)
        {
            return;
        }
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
            case AppCommand.ScrollLeft:
                HorizontalScrollTarget.ScrollHorizontal(-(count ?? 1)); // clamps at column 0
                HorizontalScrollTarget.SetNeedsDraw();
                return true;
            case AppCommand.ScrollRight:
                HorizontalScrollTarget.ScrollHorizontal(count ?? 1); // DiffListDataSource.Render honors Viewport.X
                HorizontalScrollTarget.SetNeedsDraw();
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
            case AppCommand.Open:
                // On the diff pane, open the selected line's existing comment thread(s);
                // on the file list, fall through so Accepting opens the file / toggles a folder.
                if (!_diffPane.HasFocus)
                {
                    return false;
                }
                ViewThreadAtCursor();
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
            case AppCommand.ToggleDiffMode:
                ToggleDiffMode();
                return true;
            case AppCommand.ExpandContext:
                ExpandFoldAtCursor();
                return true;
            case AppCommand.ExpandAllContext:
                ExpandAllFolds();
                return true;
            case AppCommand.SearchDiff:
                BeginSearch();
                return true;
            case AppCommand.SearchNext:
                StepSearch(forward: true);
                return true;
            case AppCommand.SearchPrev:
                StepSearch(forward: false);
                return true;
            case AppCommand.NextHunk:
                NavHunk(forward: true, count ?? 1);
                return true;
            case AppCommand.PrevHunk:
                NavHunk(forward: false, count ?? 1);
                return true;
            case AppCommand.NextThread:
                NavThread(forward: true, count ?? 1);
                return true;
            case AppCommand.PrevThread:
                NavThread(forward: false, count ?? 1);
                return true;
            case AppCommand.NextUnviewedFile:
                _ = StepUnviewedFile(1);
                return true;
            case AppCommand.PrevUnviewedFile:
                _ = StepUnviewedFile(-1);
                return true;
            case AppCommand.MarkViewed:
                MarkCurrentViewed();
                return true;
            case AppCommand.MarkUnviewed:
                MarkCurrentUnviewed();
                return true;
            case AppCommand.ToggleThreadFilter:
                ToggleThreadFilter();
                return true;
            case AppCommand.Vote:
                Vote();
                return true;
            case AppCommand.Comment:
                _ = FireAndForget.Observe(CommentAsync(), app, log);
                return true;
            case AppCommand.OpenBranch:
                OpenSourceBranch();
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

    private async Task LoadAsync()
    {
        await vm.LoadAsync(Token).IgnoreCancellationAsync();
        // Fill per-file diff stats (StatsFor / totals) in the background so file rows and the
        // header totals populate; each computed file raises Changed → a re-render.
        _ = FireAndForget.Observe(vm.PrefetchAllDiffsAsync(Token).IgnoreCancellationAsync(), app, log);
    }

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
        var lineIndex = SelectedDiffLine();
        if (lineIndex < 0)
        {
            // The cursor is on a fold marker (or otherwise off any real line); commenting
            // there would silently no-op, so refuse before opening the editor.
            log("no diff line here to comment on — expand the fold or move to a line");
            return;
        }
        string? text;
        try
        {
            text = await textInput.ReadAsync(new TextInputRequest("comment", SingleLine: false), Token).ConfigureAwait(false);
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

    /// <summary>o/Enter on the diff pane: show the existing comment thread(s) anchored to the selected line.</summary>
    private void ViewThreadAtCursor()
    {
        if (vm.CurrentDiff is not { Lines.Count: > 0 } diff)
        {
            return;
        }
        var index = SelectedDiffLine();
        if (index < 0 || index >= diff.Lines.Count)
        {
            return;
        }
        var threads = vm.ThreadsForDiffLine(diff.Lines[index]);
        if (threads.Count == 0)
        {
            log("no comments on this line");
            return;
        }

        // The seam receives the formatted text (headless tests); the real path opens the
        // interactive thread overlay (reply / resolve / reactivate) on the anchored thread(s).
        if (ViewThreadAction is not null)
        {
            ViewThreadAction(FormatThreads(threads));
        }
        else
        {
            new ThreadViewDialog(app, vm, textInput, log, threads).Show();
        }
    }

    private static string FormatThreads(IReadOnlyList<PrThread> threads)
    {
        var lines = new List<string>();
        foreach (var thread in threads)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }
            lines.Add($"#{thread.Id} [{thread.Status}]");
            lines.AddRange(thread.Comments
                .Where(c => !c.IsSystem)
                .Select(c => $"  {c.Author}: {c.Content}"));
        }
        return string.Join('\n', lines);
    }

    /// <summary>s: flip the diff pane between unified and side-by-side, keeping the cursor on the same line.</summary>
    private void ToggleDiffMode()
    {
        var focused = SelectedDiffLine();
        _sideBySide = !_sideBySide;
        Render();
        SelectDiffLine(focused);
        _diffPane.SetNeedsDraw();
        log(_sideBySide ? "side-by-side diff" : "unified diff");
    }

    /// <summary>e: expand the fold whose marker is selected, or the first collapsed fold if the cursor is elsewhere.</summary>
    private void ExpandFoldAtCursor()
    {
        if (_sideBySide || _foldState is null)
        {
            return;
        }
        var sel = _diffPane.SelectedItem ?? -1;
        var foldId = sel >= 0 && sel < _diffRows.Count ? _diffRows[sel].FoldId : null;
        foldId ??= _diffRows.FirstOrDefault(r => r.FoldId is not null)?.FoldId;
        if (foldId is { } id)
        {
            _foldState = _foldState.Expand(id);
            Render();
            _diffPane.SetNeedsDraw();
        }
    }

    /// <summary>E: expand every fold in the current file (show full context).</summary>
    private void ExpandAllFolds()
    {
        if (_sideBySide || _foldState is null)
        {
            return;
        }
        _foldState = _foldState.ExpandAll();
        Render();
        _diffPane.SetNeedsDraw();
    }

    /// <summary>/: reveal the inline search bar (or, under test, take the query from the seam).</summary>
    private void BeginSearch()
    {
        if (SearchPromptAction is not null)
        {
            ApplySearch(SearchPromptAction());
            return;
        }
        _searchBar.Text = "";
        _searchBar.Visible = true;
        _searchBar.SetFocus();
    }

    /// <summary>Hide the inline search bar and return focus to the diff pane (so n/N work).</summary>
    private void HideSearchBar()
    {
        _searchBar.Visible = false;
        _diffPane.SetFocus();
    }

    /// <summary>h/l scroll whichever pane has focus horizontally (the diff pane by default, or the
    /// file list when it holds focus — so long tree rows can scroll too), mirroring VimScroll.</summary>
    private Terminal.Gui.ViewBase.View HorizontalScrollTarget => _diffPane.HasFocus ? _diffPane : _fileList;

    /// <summary>g b: open the PR's source branch in the browser (or the test seam).</summary>
    private void OpenSourceBranch()
    {
        if (context is null)
        {
            log("no context configured — cannot open the source branch");
            return;
        }
        var pr = vm.PullRequest;
        var url = AdoUrls.Branch(context, pr.ProjectName, pr.RepositoryName, pr.SourceBranch);
        if (OpenUrlAction is not null)
        {
            OpenUrlAction(url);
            return;
        }
        if (BrowserLauncher.TryOpen(url, out var error))
        {
            log($"opening branch {pr.SourceBranch}");
        }
        else
        {
            log($"could not open browser: {error}");
        }
    }

    private void ApplySearch(string? query)
    {
        if (vm.CurrentDiff is not { } diff || string.IsNullOrWhiteSpace(query))
        {
            _searchQuery = null;
            _searchMatches = [];
            Render();
            return;
        }
        _searchQuery = query.Trim();
        _searchMatches = DiffSearch.Find(diff.Lines, _searchQuery);
        _searchIndex = 0;
        Render();
        if (_searchMatches.Count > 0)
        {
            EnsureVisibleAndSelect(_searchMatches[_searchIndex].LineIndex);
        }
        else
        {
            log($"no matches for \"{_searchQuery}\"");
        }
    }

    /// <summary>n/N: move to the next/previous search match, wrapping, expanding a fold that hides it.</summary>
    private void StepSearch(bool forward)
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }
        _searchIndex = forward
            ? DiffSearch.Next(_searchMatches, _searchIndex)
            : DiffSearch.Prev(_searchMatches, _searchIndex);
        EnsureVisibleAndSelect(_searchMatches[_searchIndex].LineIndex);
    }

    /// <summary>Select the row showing a unified line, expanding all folds first if that line is hidden.</summary>
    private void EnsureVisibleAndSelect(int lineIndex)
    {
        if (!_sideBySide && _foldState is not null && !IsLineVisible(lineIndex))
        {
            _foldState = _foldState.ExpandContaining(lineIndex);
            Render();
        }
        SelectDiffLine(lineIndex);
        _diffPane.SetNeedsDraw();
    }

    private bool IsLineVisible(int lineIndex) =>
        _diffRows.Any(r => r.LineIndex == lineIndex || r.LeftIndex == lineIndex || r.RightIndex == lineIndex);

    /// <summary>]c/[c: move the diff-pane selection to the next/previous change hunk, count-aware.</summary>
    private void NavHunk(bool forward, int count)
    {
        if (vm.CurrentDiff is not { Lines.Count: > 0 } diff)
        {
            return;
        }
        var from = CurrentUnifiedLine();
        var target = forward
            ? DiffNavigator.NextHunk(diff.Lines, from, count)
            : DiffNavigator.PrevHunk(diff.Lines, from, count);
        EnsureVisibleAndSelect(target);
    }

    /// <summary>]t/[t: move the diff-pane selection to the next/previous commented line, count-aware.</summary>
    private void NavThread(bool forward, int count)
    {
        if (vm.CurrentDiff is not { Lines.Count: > 0 } diff)
        {
            return;
        }
        bool HasThread(int i) => vm.ThreadsForDiffLine(diff.Lines[i]).Count > 0;
        var from = CurrentUnifiedLine();
        var target = forward
            ? DiffNavigator.NextThread(diff.Lines, from, HasThread, count)
            : DiffNavigator.PrevThread(diff.Lines, from, HasThread, count);
        EnsureVisibleAndSelect(target);
    }

    /// <summary>The unified diff-line nearest the cursor for navigation (skips forward off a fold marker).</summary>
    private int CurrentUnifiedLine()
    {
        var sel = _diffPane.SelectedItem ?? 0;
        for (var i = sel; i < _diffRows.Count; i++)
        {
            if (_diffRows[i].Anchor is { } a)
            {
                return a;
            }
        }
        for (var i = sel - 1; i >= 0; i--)
        {
            if (_diffRows[i].Anchor is { } a)
            {
                return a;
            }
        }
        return 0;
    }

    /// <summary>]v/[v: select the next/previous file whose diff has not been marked viewed.</summary>
    private async Task StepUnviewedFile(int delta)
    {
        var fileRows = _rows.Where(r => r.FileIndex is not null).ToList();
        if (fileRows.Count == 0)
        {
            return;
        }
        var currentPath = _fileIndex >= 0 && _fileIndex < vm.Files.Count ? vm.Files[_fileIndex].Path : null;
        var current = fileRows.FindIndex(r => string.Equals(r.NodePath, currentPath, StringComparison.Ordinal));
        for (var i = (current < 0 ? 0 : current) + delta; i >= 0 && i < fileRows.Count; i += delta)
        {
            if (!vm.IsViewed(fileRows[i].NodePath))
            {
                await SelectFile(FileIndexForPath(fileRows[i].NodePath));
                return;
            }
        }
    }

    /// <summary>m: mark the file shown in the diff pane viewed (a ✓ in the tree).</summary>
    private void MarkCurrentViewed()
    {
        if (vm.SelectedFile?.Path is not { } path)
        {
            return;
        }
        vm.MarkViewed(path);
        Render(includeDiffPane: false);
        log($"marked viewed: {path}");
    }

    /// <summary>M: mark the file shown in the diff pane unviewed (clears its ✓ in the tree).</summary>
    private void MarkCurrentUnviewed()
    {
        if (vm.SelectedFile?.Path is not { } path)
        {
            return;
        }
        vm.MarkUnviewed(path);
        Render(includeDiffPane: false);
        log($"marked unviewed: {path}");
    }

    /// <summary>T: toggle the file list between all files and only those with unresolved threads.</summary>
    private void ToggleThreadFilter()
    {
        vm.OnlyUnresolvedFiles = !vm.OnlyUnresolvedFiles;
        Render(); // Render re-points off a now-hidden file (RepointIfFilteredOut) if needed.
        log(vm.OnlyUnresolvedFiles ? "showing files with unresolved threads" : "showing all files");
    }

    /// <summary>
    /// When the unresolved-only filter is active and the displayed file no longer survives it
    /// — because it was just filtered on, or its last unresolved thread was resolved — move off
    /// it to the first still-visible file so the diff pane never strands on a hidden file.
    /// SelectFile sets <c>_fileIndex</c> synchronously, so the surrounding Render tracks it.
    /// </summary>
    private void RepointIfFilteredOut()
    {
        if (vm.OnlyUnresolvedFiles && vm.FilteredFiles.Count > 0 && vm.SelectedFile is { } file &&
            !vm.FilteredFiles.Any(f => string.Equals(f.Path, file.Path, StringComparison.Ordinal)))
        {
            _ = SelectFile(FileIndexForPath(vm.FilteredFiles[0].Path));
        }
    }

    /// <summary>v: pick a vote (MessageBox, or the test chooser) and apply it to the PR.</summary>
    private void Vote()
    {
        var choose = VoteChooser ?? new Func<string, IReadOnlyList<string>, int?>(
            (title, options) => MessageBox.Query(app, title, "", [.. options]));
        if (choose("vote", VoteLabels) is { } index && index >= 0 && index < VoteValues.Length)
        {
            var vote = VoteValues[index];
            if (VoteAction is not null)
            {
                VoteAction(vote);
                return;
            }
            _ = FireAndForget.Observe(RunVoteAsync(vote, VoteLabels[index]), app, log);
        }
    }

    private async Task RunVoteAsync(PrVote vote, string label)
    {
        await vm.VoteAsync(vote, Token).ConfigureAwait(false);
        app.Invoke(() => log(vm.Error is { } e ? $"vote failed: {e}" : $"voted: {label}"));
    }

    private int FileIndexForPath(string path)
    {
        for (var i = 0; i < vm.Files.Count; i++)
        {
            if (string.Equals(vm.Files[i].Path, path, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A dim placeholder row standing in for a run of hidden context lines.</summary>
    private static StyledLine FoldMarkerLine(int hidden)
    {
        var text = $"    ··· {hidden} lines ···";
        return new StyledLine(
            text,
            [new StyledRun(0, text.Length, new RunStyle(TokenKind.Comment, DiffLineKind.Context, Emphasis: false, IsGutter: false))]);
    }

    /// <summary>
    /// The unified diff-line index the diff-pane cursor points at, in either mode (new side
    /// preferred). Returns -1 for an anchorless row (a fold marker) so comment/thread guards
    /// bail cleanly rather than anchoring to line 0.
    /// </summary>
    private int SelectedDiffLine()
    {
        var sel = _diffPane.SelectedItem ?? 0;
        if (sel >= 0 && sel < _diffRows.Count)
        {
            return _diffRows[sel].Anchor ?? -1;
        }
        return -1;
    }

    /// <summary>Move the diff-pane cursor to the row showing a given unified diff line, in either mode.</summary>
    private void SelectDiffLine(int unifiedIndex)
    {
        if (_diffPane.Source is not { Count: > 0 } source)
        {
            return;
        }
        var target = 0;
        for (var i = 0; i < _diffRows.Count; i++)
        {
            var row = _diffRows[i];
            if (row.LineIndex == unifiedIndex || row.RightIndex == unifiedIndex || row.LeftIndex == unifiedIndex)
            {
                target = i;
                break;
            }
        }
        _diffPane.SelectedItem = Math.Clamp(target, 0, source.Count - 1);
    }

    /// <summary>The dialog title: PR id, running +Σ/-Σ diff totals, unresolved count, then the key hints.</summary>
    private string TitleFor() =>
        $"diff review !{vm.PrId}  +{vm.TotalAdditions} -{vm.TotalDeletions}  {vm.UnresolvedThreadCount} unresolved — " +
        "q close · Tab panes · h/l scroll · [f/]f file · [c/]c hunk · [t/]t thread · [v/]v unviewed · / search · n/N · " +
        "z fold · e/E context · s split · c comment · o thread · gb branch · v vote · m viewed · T filter · ? keys";

    /// <summary>Test seam: refresh title totals and file-row stats without re-tokenizing the diff.</summary>
    internal void RefreshStats() => Render(includeDiffPane: false);

    /// <summary>
    /// The header above the diff pane: which file is on screen and its stats. Keyed on the diff's
    /// own path so it never labels one file's diff with another's name. Precedence matches what a
    /// full render has always produced — a diff on screen wins, then loading, then an error, and
    /// with none of those the header keeps whatever it last said.
    /// </summary>
    private void WriteDiffHeader()
    {
        if (vm.CurrentDiff is { } diff && vm.CurrentDiffPath is { } path)
        {
            var mode = _sideBySide ? "  (side-by-side)" : "";
            _diffHeader.Text = $" {path}   +{diff.Additions} -{diff.Deletions}" +
                (diff.IsBinary ? "  (binary)" : diff.TooLarge ? "  (too large)" : mode);
        }
        else if (vm.IsLoading)
        {
            _diffHeader.Text = " loading diff…";
        }
        else if (vm.Error is { } e)
        {
            _diffHeader.Text = $" error: {e}";
        }
    }

    /// <param name="includeDiffPane">
    /// When <see langword="false"/>, refresh only the chrome (title totals, file-row stats) and
    /// skip rebuilding the diff pane — used by the background stats prefetch, whose updates never
    /// change the displayed diff content, so the open file is not re-tokenized on every file.
    /// </param>
    private void Render(bool includeDiffPane = true)
    {
        ApplyResponsiveLayout();
        if (_dialog is not null)
        {
            _dialog.Title = TitleFor();
        }

        // Set in one place, on every render: a partial (chrome-only) refresh describes the same
        // file as a full one, so marking a file viewed cannot leave a stale error where the path
        // belongs. The error text is the fallback for having no diff to show — an expected failure
        // reaches the reviewer through the message bar (ADR 0013), which is its surface.
        WriteDiffHeader();

        // Rebuild the file tree, keeping the highlight on the displayed file's row.
        RepointIfFilteredOut();
        RebuildFileList(SelectedFileNodePath());

        // Everything below describes the diff on screen, so it keys off the path that diff came
        // from — never the file-tree cursor. SelectFileAsync moves the cursor to the new file and
        // publishes its diff only once fetched, so in that window vm.SelectedFile is already the
        // next file while this is still the previous one; keying on it would anchor this file's
        // lines to the next file's comment threads.
        if (includeDiffPane && vm.CurrentDiff is { } diff && vm.CurrentDiffPath is { } diffPath)
        {
            // Rebuild the diff pane (thread markers may have changed after a comment),
            // but preserve the reviewer's line position on a same-file refresh; reset
            // to the top only when the displayed file actually changed.
            var sameFile = diffPath == _renderedDiffPath;
            var keepLine = sameFile ? _diffPane.SelectedItem : 0;
            if (!sameFile)
            {
                // Search matches are line-index scoped to one file; drop them when the file changes.
                _searchQuery = null;
                _searchMatches = [];
            }
            _renderedDiffPath = diffPath;

            // Point the style cache at this render's inputs: it reuses every composition whose
            // line, language and thread marker are unchanged, so a render that only expands a
            // fold, filters the tree or lands a comment no longer re-tokenizes the whole file.
            // The commented lines are looked up once here rather than scanned per line.
            var language = LanguageDetector.FromPath(diffPath);
            var (commentedLeft, commentedRight) = vm.CommentedLinesFor(diffPath);
            _styleCache.Prepare(diff.Lines, language, commentedLeft, commentedRight);
            var rows = new List<DiffRow>();
            List<StyledLine> styled;
            if (_sideBySide)
            {
                // Side-by-side shows full context (no fold), so drop any fold state.
                _foldState = null;
                var sbs = SideBySideComposer.Pair(diff.Lines);
                foreach (var r in sbs)
                {
                    rows.Add(new DiffRow(null, r.LeftIndex, r.RightIndex, null, null));
                }
                var columnWidth = Math.Max(1, (_diffContentWidth - SideBySideComposer.Separator.Length) / 2);
                styled = [.. _styleCache.SideBySide(sbs, columnWidth)];
            }
            else
            {
                // Unified folds distant context (radius 3) by default; e/E expand. Rebuild the
                // fold state only when the file changes (or after a mode toggle nulled it), so
                // e/E expansions survive same-file refreshes (thread markers, mark-viewed).
                if (!sameFile || _foldState is null)
                {
                    _foldState = DiffFoldState.Create(diff.Lines);
                }
                var hitsByLine = _searchMatches.Count == 0
                    ? null
                    : _searchMatches.GroupBy(m => m.LineIndex)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<LineSpan>)[.. g.Select(m => m.Span)]);
                styled = [];
                foreach (var foldRow in _foldState.Rows())
                {
                    if (foldRow.Line is not null && foldRow.LineIndex is { } li)
                    {
                        rows.Add(new DiffRow(li, null, null, null, null));
                        // Search hits stay an overlay on the cached composition: a query change
                        // must not invalidate a line's styling, only decorate it.
                        var composed = _styleCache.Unified(li);
                        if (hitsByLine is not null && hitsByLine.TryGetValue(li, out var spans))
                        {
                            composed = DiffLineStyler.WithSearchHits(composed, spans, composed.Runs[0].Length);
                        }
                        styled.Add(composed);
                    }
                    else
                    {
                        rows.Add(new DiffRow(null, null, null, foldRow.FoldId, foldRow.HiddenCount));
                        styled.Add(FoldMarkerLine(foldRow.HiddenCount ?? 0));
                    }
                }
            }
            _diffRows = rows;
            _diffPane.Source = new DiffListDataSource(styled);
            if (styled.Count > 0)
            {
                _diffPane.SelectedItem = Math.Clamp(keepLine ?? 0, 0, styled.Count - 1);
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
        // When the unresolved filter is on, build the tree from the filtered projection; leaves
        // still carry their real path as NodePath, so opening resolves to vm.Files by path.
        var files = vm.OnlyUnresolvedFiles ? vm.FilteredFiles : vm.Files;
        _rows = FileTree.Flatten(files, _collapsedDirs, BuildAnnotations());
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
        else if (row.FileIndex is not null)
        {
            // Resolve by path (not the row's FileIndex, which is filtered-relative) so the diff
            // pane and _fileIndex always track the same identity in vm.Files.
            var fileIndex = FileIndexForPath(row.NodePath);
            if (fileIndex >= 0)
            {
                _ = SelectFile(fileIndex);
            }
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
        var currentPath = _fileIndex >= 0 && _fileIndex < vm.Files.Count ? vm.Files[_fileIndex].Path : null;
        var current = fileRows.FindIndex(r => string.Equals(r.NodePath, currentPath, StringComparison.Ordinal));
        var next = Math.Clamp((current < 0 ? 0 : current) + delta, 0, fileRows.Count - 1);
        await SelectFile(FileIndexForPath(fileRows[next].NodePath));
    }

    /// <summary>Per-file review metadata (diff stat, viewed, unresolved) keyed by path for the tree.</summary>
    private IReadOnlyDictionary<string, FileAnnotation> BuildAnnotations()
    {
        var map = new Dictionary<string, FileAnnotation>(StringComparer.Ordinal);
        foreach (var file in vm.Files)
        {
            var stats = vm.StatsFor(file.Path);
            map[file.Path] = new FileAnnotation(
                stats?.Additions, stats?.Deletions, vm.IsViewed(file.Path), HasUnresolvedThread(file.Path));
        }
        return map;
    }

    private bool HasUnresolvedThread(string path) =>
        vm.Threads.Any(t =>
            t.Status == PrThreadStatus.Active && !t.IsSystemOnly &&
            string.Equals(t.FilePath, path, StringComparison.Ordinal));

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
        var viewed = row.Viewed ? "[✓] " : "[ ] ";
        var unresolved = row.HasUnresolved ? " ●" : "";
        var stats = row.Additions is { } a && row.Deletions is { } d ? $"  +{a} -{d}" : "";
        return $"{indent}{viewed}{glyph} {row.Label}{unresolved}{stats}";
    }
}

/// <summary>
/// One visible diff-pane row mapped back to the original unified <see cref="DiffLine"/> list.
/// The single map every consumer (comment/thread anchoring, hunk/thread/search navigation)
/// resolves through, so anchoring stays correct across unified, folded, and side-by-side modes.
/// A unified line sets <see cref="LineIndex"/>; a side-by-side row sets <see cref="LeftIndex"/>/
/// <see cref="RightIndex"/>; a fold marker sets <see cref="FoldId"/>/<see cref="HiddenCount"/>
/// and is anchorless.
/// </summary>
internal sealed record DiffRow(int? LineIndex, int? LeftIndex, int? RightIndex, int? FoldId, int? HiddenCount)
{
    /// <summary>The unified diff-line this row anchors to (new/right side preferred); null for a fold marker.</summary>
    public int? Anchor => LineIndex ?? RightIndex ?? LeftIndex;
}

/// <summary>
/// Collapses a burst of events into a single queued refresh: the first caller to
/// <see cref="TryQueue"/> owns the refresh, every caller after it is told one is already coming.
/// <see cref="Release"/> reopens the gate and must run *before* the refresh does its work, so an
/// event raised while it runs still queues a later one rather than being swallowed. Interlocked
/// because the raising thread and the releasing (UI) thread are not the same.
/// </summary>
internal sealed class CoalescingGate
{
    private int _queued;

    /// <summary>True if the caller should queue the refresh; false if one is already queued.</summary>
    public bool TryQueue() => Interlocked.CompareExchange(ref _queued, 1, 0) == 0;

    /// <summary>Reopens the gate; call at the start of the queued refresh, not the end.</summary>
    public void Release() => Interlocked.Exchange(ref _queued, 0);
}
