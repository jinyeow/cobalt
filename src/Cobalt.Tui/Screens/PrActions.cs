using Cobalt.Core.Models;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Screens;

/// <summary>
/// The pull-request vote flow in one place, shared by the detail dialog and the
/// list (single source of truth). The dialog passes its own bound
/// <see cref="PrDetailViewModel"/>; the list entry point (<see cref="RunVoteAsync"/>)
/// constructs and loads a throwaway view-model for the selected PR — the by-id load
/// is org-level, so it carries the PR's own project/repository through to the vote
/// (cross-project drill-in, ADR 0011).
/// </summary>
public sealed class PrActions
{
    private static readonly string[] Labels =
        ["approve", "approve w/ suggestions", "wait for author", "reject", "reset"];

    private static readonly PrVote[] Votes =
        [PrVote.Approved, PrVote.ApprovedWithSuggestions, PrVote.WaitingForAuthor, PrVote.Rejected, PrVote.NoVote];

    private readonly Action<string> _log;
    private readonly Func<string, IReadOnlyList<string>, int?> _choose;
    private readonly Action<Action> _post;

    /// <param name="app">Host application; supplies the default vote chooser and UI-thread marshaling.</param>
    /// <param name="log">Message sink for success/failure lines.</param>
    /// <param name="choose">Option picker (title, options) → chosen index, or null/-1 when dismissed; defaults to a <see cref="MessageBox"/> query. Injectable for tests.</param>
    /// <param name="post">Marshals a callback onto the UI thread; defaults to <c>IApplication.Invoke</c>. Injectable for tests.</param>
    public PrActions(
        IApplication app,
        Action<string> log,
        Func<string, IReadOnlyList<string>, int?>? choose = null,
        Action<Action>? post = null)
    {
        _log = log;
        _choose = choose ?? new Func<string, IReadOnlyList<string>, int?>(
            (title, options) => MessageBox.Query(app, title, "", [.. options]));
        _post = post ?? app.Invoke;
    }

    /// <summary>List entry point: load the PR by id, then run the vote flow.</summary>
    public async Task RunVoteAsync(IPullRequestStore store, int prId, CancellationToken ct)
    {
        var vm = new PrDetailViewModel(store, prId);
        await vm.LoadAsync(ct).ConfigureAwait(false);
        await VoteAsync(vm, ct).ConfigureAwait(false);
    }

    /// <summary>The vote flow (shared with the dialog): pick a vote, then apply it.</summary>
    public async Task VoteAsync(PrDetailViewModel vm, CancellationToken ct)
    {
        if (_choose("vote", Labels) is { } index && index >= 0 && index < Votes.Length)
        {
            await RunAndLog(vm, vm.VoteAsync(Votes[index], ct), $"voted: {Labels[index]}").ConfigureAwait(false);
        }
    }

    private async Task RunAndLog(PrDetailViewModel vm, Task work, string success)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // caller closed mid-op; nothing to report
        }
        _post(() => _log(vm.Error is { } e ? $"failed: {e}" : success));
    }
}
