using Cobalt.Core.Models;
using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Interactive overlay for the comment thread(s) anchored to a diff line: shows the
/// thread text (scrollable via the shared VimScroll seam) and handles `c` reply,
/// `x` resolve, `u` reactivate against the view-model. When a line carries several
/// threads the actions target the first (called out in the title).
/// </summary>
public sealed class ThreadViewDialog(
    IApplication app,
    PrDiffViewModel vm,
    EditorService editor,
    Action<string> log,
    IReadOnlyList<PrThread> threads)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly KeymapRouter _router = new(KeyBindingTable.Default());
    private readonly IReadOnlyList<int> _threadIds = threads.Select(t => t.Id).ToList();
    private bool _closed;
    private Dialog? _dialog;
    private string _lastBody = "";
#pragma warning disable CS0618 // read-only scrollable pane; see WorkItemDetailDialog
    private TextView? _body;

    private CancellationToken Token => _cts.Token;

    /// <summary>Test seam: the read-only scroll pane, exposed so a view-level test can drive scrolling / read text.</summary>
    internal TextView Body => _body ?? throw new InvalidOperationException("Build() first");
#pragma warning restore CS0618

    /// <summary>Test seam: the thread the actions target (the first anchored to the line).</summary>
    internal int TargetThreadId => threads[0].Id;

    /// <summary>Test seam: replaces the default close (app.RequestStop) so a test can observe close without a run loop.</summary>
    internal Action? CloseAction { get; set; }

    /// <summary>Test seam: replaces the real reply path (needs the editor) so a test can observe the 'c' key.</summary>
    internal Action? ReplyAction { get; set; }

    /// <summary>Test seam: replaces the real resolve path (needs app.Invoke) so a test can observe the 'x' key.</summary>
    internal Action? ResolveAction { get; set; }

    /// <summary>Test seam: replaces the real reactivate path (needs app.Invoke) so a test can observe the 'u' key.</summary>
    internal Action? ReactivateAction { get; set; }

    /// <summary>Test seam: replaces the real help overlay (needs a run loop) so a test can observe '?'.</summary>
    internal Action? HelpAction { get; set; }

    public void Show()
    {
        using var dialog = Build();
        try
        {
            app.Run(dialog);
        }
        finally
        {
            _closed = true;
            vm.Changed -= OnChanged;
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Builds and wires the overlay (body pane, verb keys) without starting the run loop,
    /// so a view-level test can drive key delivery headlessly.
    /// </summary>
    internal Dialog Build()
    {
        var first = threads[0];
        _lastBody = FormatThreads(threads);
        var extra = threads.Count > 1 ? $" (of {threads.Count}, acting on #{first.Id})" : "";
        var dialog = new Dialog
        {
            Title = $"thread #{first.Id}{extra} — q close · c reply · x resolve · u reactivate · ? keys",
            Width = Dim.Percent(80),
            Height = Dim.Percent(80),
        };
        _dialog = dialog;

#pragma warning disable CS0618 // read-only scrollable pane; see WorkItemDetailDialog
        var body = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = true, // position indicator; content is scrolled pager-style (VimScroll)
            Text = _lastBody,
        };
#pragma warning restore CS0618
        _body = body;

        // A focused ReadOnly TextView swallows printable runes before dialog.KeyDown runs, so
        // subscribe the verb handler to the TextView too (mirrors PrDetailDialog).
        body.KeyDown += HandleKey;
        dialog.KeyDown += HandleKey;

        // A resolve/reactivate/reply refetches vm.Threads and raises Changed; re-render the
        // body from that live state so the open overlay's [status] updates in place instead of
        // showing the snapshot it opened with (mirrors DiffReviewDialog's Changed subscription).
        vm.Changed += OnChanged;

        dialog.Add(body);
        return dialog;
    }

    private void OnChanged() => app.Invoke(() =>
    {
        if (!_closed)
        {
            RefreshBody();
        }
    });

    /// <summary>
    /// Re-renders the body from the VM's current threads, matched by the ids this overlay
    /// opened on, so a mutation's new status/comments replace the stale opening snapshot.
    /// Internal so a view-level test can drive it without the app.Invoke run loop.
    /// </summary>
    internal void RefreshBody()
    {
        if (_body is null)
        {
            return;
        }
        var current = vm.ThreadsByIds(_threadIds);
        if (current.Count == 0)
        {
            return; // retain the last-good body when the threads have (transiently) vanished
        }
        var text = FormatThreads(current);
        if (text == _lastBody)
        {
            return; // unrelated tick: identical text; skip the reassign that would reset scroll
        }
        _lastBody = text;
        _body.Text = text;
        _dialog?.SetNeedsDraw();
    }

    private void HandleKey(object? sender, Terminal.Gui.Input.Key key)
    {
        var token = KeyTokenizer.ToToken(key);
        if (token is null)
        {
            return;
        }
        var hadPending = _router.HasPending;
        var result = _router.Feed(token, KeyScope.ThreadView);
        switch (result.Kind)
        {
            case KeyResultKind.Pending:
                key.Handled = true;
                break;
            case KeyResultKind.Matched when Dispatch(result.Command):
                key.Handled = true;
                break;
            case KeyResultKind.Matched:
                break;
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

    /// <summary>Runs the matched command; returns true when the dialog acted.</summary>
    private bool Dispatch(AppCommand command)
    {
        if (_body is not null && VimScroll.Applies(command))
        {
            VimScroll.Apply(_body, command, null);
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
                    TextDialog.Show(app, "keys", HelpText.ForDialog(_router.Table, KeyScope.ThreadView));
                }
                return true;
            case AppCommand.Comment:
                if (ReplyAction is not null)
                {
                    ReplyAction();
                }
                else
                {
                    _ = FireAndForget.Observe(ReplyAsync(), app, log);
                }
                return true;
            case AppCommand.ResolveThread:
                if (ResolveAction is not null)
                {
                    ResolveAction();
                }
                else
                {
                    _ = FireAndForget.Observe(
                        RunAndLog(vm.ResolveThreadAsync(threads[0].Id, Token), "thread resolved"), app, log);
                }
                return true;
            case AppCommand.ReactivateThread:
                if (ReactivateAction is not null)
                {
                    ReactivateAction();
                }
                else
                {
                    _ = FireAndForget.Observe(
                        RunAndLog(vm.ReactivateThreadAsync(threads[0].Id, Token), "thread reactivated"), app, log);
                }
                return true;
            default:
                return false;
        }
    }

    private void RequestClose()
    {
        if (CloseAction is not null)
        {
            CloseAction();
        }
        else if (_dialog is not null)
        {
            app.RequestStop(_dialog);
        }
    }

    private async Task ReplyAsync()
    {
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
        if (!string.IsNullOrWhiteSpace(text))
        {
            await RunAndLog(vm.ReplyToThreadAsync(threads[0].Id, text.Trim(), Token), "reply posted").ConfigureAwait(false);
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
        app.Invoke(() => log(vm.Error is { } e ? $"failed: {e}" : success));
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
}
