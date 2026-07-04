using Cobalt.Tui.App;
using Cobalt.Tui.Editor;
using Cobalt.Core.Models;
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
    private bool _closed;

    private CancellationToken Token => _cts.Token;

    public void Show()
    {
        using var dialog = new Dialog
        {
            Title = $"PR !{vm.Id} — q close · d diff · v vote · c reply · x resolve · u reactivate · C complete · A abandon",
            Width = Dim.Percent(92),
            Height = Dim.Percent(92),
        };

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

        void OnChanged() => app.Invoke(() =>
        {
            if (!_closed)
            {
                body.Text = RenderBody();
                dialog.SetNeedsDraw();
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
                case "v":
                    key.Handled = true;
                    PickVote();
                    break;
                case "c":
                    key.Handled = true;
                    _ = ReplyAsync();
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
                    OpenDiff();
                    break;
                default:
                    break;
            }
        };

        dialog.Add(body);
        _ = LoadAsync();
        body.Text = RenderBody();
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
            // dialog closed mid-load
        }
    }

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

    private void PickVote()
    {
        string[] labels = ["approve", "approve w/ suggestions", "wait for author", "reject", "reset"];
        PrVote[] votes = [PrVote.Approved, PrVote.ApprovedWithSuggestions, PrVote.WaitingForAuthor, PrVote.Rejected, PrVote.NoVote];
        var choice = MessageBox.Query(app, "vote", "", labels);
        if (choice is { } i && i >= 0 && i < votes.Length)
        {
            _ = RunAndLog(vm.VoteAsync(votes[i], Token), $"voted: {labels[i]}");
        }
    }

    private async Task ReplyAsync()
    {
        var thread = await PromptThreadId("reply to thread #").ConfigureAwait(false);
        if (thread is null)
        {
            return;
        }
        var text = await editor.EditAsync("", ".md", Token).ConfigureAwait(false);
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
        var text = await editor.EditAsync("", ".txt", Token).ConfigureAwait(false);
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
        await work.ConfigureAwait(false);
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
