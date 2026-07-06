using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Core.Models;
using Cobalt.Tui.Tasks;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// Modal PR detail: reviewers/votes, merge status, comment threads, and the
/// actions (vote, reply, resolve/reactivate, complete, abandon).
/// </summary>
public sealed class PrDetailDialog(
    IApplication app,
    PrDetailViewModel vm,
    EditorService editor,
    Action<string> log,
    IPrDiffSource? diffSource = null)
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PrActions _actions = new(app, log);
    private bool _closed;
    private Dialog? _dialog;
#pragma warning disable CS0618 // read-only scrollable pane; see WorkItemDetailDialog
    private TextView? _body;

    private CancellationToken Token => _cts.Token;

    /// <summary>Test seam: the read-only scroll pane, exposed so a view-level test can drive scrolling.</summary>
    internal TextView Body => _body ?? throw new InvalidOperationException("Build() first");
#pragma warning restore CS0618

    /// <summary>Test seam: replaces the default close (app.RequestStop) so a test can observe close without a run loop.</summary>
    internal Action? CloseAction { get; set; }

    /// <summary>Test seam: replaces the real diff-open path (needs a run loop) so a test can observe the 'd' key.</summary>
    internal Action? DiffAction { get; set; }

    /// <summary>Test seam: replaces the real reply path (needs the editor) so a test can observe the 'c' key.</summary>
    internal Action? ReplyAction { get; set; }

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
    /// Constructs and wires the dialog (body pane, verb keys, change subscription)
    /// without starting the load or run loop. Split out so view-level tests can
    /// drive key delivery headlessly.
    /// </summary>
    internal Dialog Build()
    {
        var dialog = new Dialog
        {
            Title = $"PR !{vm.Id} — q close · d diff · v vote · c reply · x resolve · u reactivate · C complete · A abandon",
            Width = Dim.Percent(92),
            Height = Dim.Percent(92),
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
        };
#pragma warning restore CS0618
        _body = body;

        vm.Changed += OnChanged;

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

    private void OnChanged() => app.Invoke(() =>
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
            case "v":
                key.Handled = true;
                _ = _actions.VoteAsync(vm, Token);
                break;
            case "c":
                key.Handled = true;
                if (ReplyAction is not null)
                {
                    ReplyAction();
                }
                else
                {
                    _ = ReplyAsync();
                }
                break;
            case "x":
                key.Handled = true;
                _ = ThreadStatusAsync(resolve: true);
                break;
            case "u":
                key.Handled = true;
                _ = ThreadStatusAsync(resolve: false);
                break;
            case "A":
                key.Handled = true;
                ConfirmAbandon();
                break;
            case "C":
                key.Handled = true;
                ConfirmComplete();
                break;
            case "d":
                key.Handled = true;
                (DiffAction ?? OpenDiff)();
                break;
            default:
                break;
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

    private string RenderBody()
    {
        if (vm.IsLoading)
        {
            return "loading…";
        }
        var pr = vm.PullRequest;
        if (pr is null)
        {
            return vm.Error is { } e ? $"error: {e}" : "no data";
        }

        var lines = new List<string>
        {
            $"!{pr.PullRequestId}  {pr.Title}" + (pr.IsDraft ? "  [draft]" : ""),
            $"{pr.RepositoryName}: {pr.SourceBranch} → {pr.TargetBranch}   status: {pr.Status}   merge: {pr.MergeStatus ?? "?"}",
            $"author: {pr.Author}",
            "",
            "Reviewers:",
        };
        lines.AddRange(pr.Reviewers.Count == 0
            ? ["  (none)"]
            : pr.Reviewers.Select(r => $"  {VoteGlyph(r.Vote)} {r.DisplayName}{(r.IsRequired ? " (required)" : "")}"));

        if (pr.LinkedWorkItemIds.Count > 0)
        {
            lines.Add("");
            lines.Add($"Linked work items: {string.Join(", ", pr.LinkedWorkItemIds.Select(i => $"#{i}"))}");
        }

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            lines.Add("");
            lines.Add("── Description ──");
            lines.Add(pr.Description!);
        }

        lines.Add("");
        lines.Add($"── Threads ({vm.UnresolvedThreadCount} unresolved) ──");
        foreach (var t in vm.Threads)
        {
            var anchor = t.FilePath is null ? "" : $" [{t.FilePath}:{t.RightLine ?? t.LeftLine}]";
            lines.Add($"  #{t.Id} [{t.Status}]{anchor}");
            lines.AddRange(t.Comments.Where(c => !c.IsSystem).Select(c => $"      {c.Author}: {c.Content}"));
        }

        if (vm.Error is { } err)
        {
            lines.Add("");
            lines.Add($"error: {err}");
        }
        return string.Join('\n', lines);
    }

    private async Task ReplyAsync()
    {
        var thread = await PromptThreadId("reply to thread #").ConfigureAwait(false);
        if (thread is null)
        {
            return;
        }
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
            await RunAndLog(vm.ReplyAsync(thread.Value, text.Trim(), Token), "reply posted").ConfigureAwait(false);
        }
    }

    private async Task ThreadStatusAsync(bool resolve)
    {
        var thread = await PromptThreadId($"{(resolve ? "resolve" : "reactivate")} thread #").ConfigureAwait(false);
        if (thread is null)
        {
            return;
        }
        var task = resolve
            ? vm.ResolveThreadAsync(thread.Value, Token)
            : vm.ReactivateThreadAsync(thread.Value, Token);
        await RunAndLog(task, resolve ? "thread resolved" : "thread reactivated").ConfigureAwait(false);
    }

    private async Task<int?> PromptThreadId(string prompt)
    {
        string? text;
        try
        {
            text = await editor.EditAsync("", ".txt", Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EditorLaunchException or System.IO.IOException)
        {
            app.Invoke(() => log($"editor failed: {ex.Message}"));
            return null;
        }
        return int.TryParse(text?.Trim(), out var id) ? id : null;
    }

    private void ConfirmAbandon()
    {
        var confirm = MessageBox.Query(app, "abandon PR", "Abandon this pull request?", "cancel", "abandon");
        if (confirm == 1)
        {
            _ = RunAndLog(vm.AbandonAsync(Token), "PR abandoned");
        }
    }

    private void ConfirmComplete()
    {
        string[] strategies = ["noFastForward", "squash", "rebase", "rebaseMerge"];
        var choice = MessageBox.Query(app, "complete PR", "merge strategy:", strategies);
        if (choice is { } i && i >= 0 && i < strategies.Length)
        {
            var confirm = MessageBox.Query(app, "complete PR", $"Complete with {strategies[i]}?", "cancel", "complete");
            if (confirm == 1)
            {
                _ = RunAndLog(vm.CompleteAsync(strategies[i], deleteSource: false, Token), "PR completed");
            }
        }
    }

    private void OpenDiff()
    {
        if (diffSource is null || vm.PullRequest is null)
        {
            log("diff review needs a loaded PR");
            return;
        }
        var diffVm = new PrDiffViewModel(diffSource, vm.PullRequest);
        new DiffReviewDialog(app, diffVm, editor, log).Show();
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

    private static string VoteGlyph(PrVote vote) => vote switch
    {
        PrVote.Approved => "✓",
        PrVote.ApprovedWithSuggestions => "✓~",
        PrVote.WaitingForAuthor => "⧗",
        PrVote.Rejected => "✗",
        _ => "·",
    };
}
