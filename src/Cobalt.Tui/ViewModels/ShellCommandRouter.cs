using Cobalt.Tui.Input;

namespace Cobalt.Tui.ViewModels;

/// <summary>What the shell does with a routed command (#76). Every value except
/// <see cref="NotRouted"/> is a side effect the shell performs and nothing more.</summary>
public enum ShellActionKind
{
    /// <summary>Not the router's business: the shell falls through to its own handling
    /// (<c>ShellViewModel.HandleCommand</c>, then its verb switch) with <see cref="ShellAction.Command"/>.</summary>
    NotRouted,

    /// <summary>Routed, and the correct behaviour is to do nothing — quietly. Distinct from
    /// <see cref="NotRouted"/>, which would surface a "not available here" message.</summary>
    Consumed,

    /// <summary>Workspace pane focus changed; map it onto Terminal.Gui focus.</summary>
    ApplyWorkspaceFocus,

    PrNextTab,
    PrPrevTab,

    /// <summary>Movement, routed to the preview pane's scroll (the preview holds focus).</summary>
    ScrollPreview,
    NavigateWorkItemList,
    NavigatePrList,

    /// <summary>Movement with no list to move — still repaints, as the shell always has.</summary>
    NavigateNothing,

    RefreshWorkItemList,
    RefreshPrList,
    StartWorkItemFilter,
    OpenWorkItem,
    OpenPr,
}

/// <summary>A routing outcome: what to do, for which command, at which count.</summary>
/// <param name="Kind">The side effect the shell performs.</param>
/// <param name="Command">The command the action applies to — <em>rewritten</em> when the router
/// re-routes one command as another (Tab → NextTab), so a <see cref="ShellActionKind.NotRouted"/>
/// fall-through acts on the rewritten command, not the original.</param>
/// <param name="Count">The vim count prefix, carried through unchanged.</param>
public readonly record struct ShellAction(ShellActionKind Kind, AppCommand Command, int? Count);

/// <summary>
/// The shell's section- and workspace-aware command routing (#76), lifted out of
/// <c>CobaltShell.Dispatch</c> so the decisions are unit-testable without a live shell. A
/// view-model rather than a pure function because the decision <em>is</em> the workspace focus
/// mutation: <see cref="WorkspaceViewModel.CyclePane"/> and friends both move focus and report
/// whether they consumed the key. UI-free, so ADR 0004's <c>ViewModelPurityTests</c> guards it.
/// <para>
/// Deliberately narrow: it owns only the arms that branch on the active section or on workspace
/// state, and returns <see cref="ShellActionKind.NotRouted"/> for everything else, so the shell's
/// remaining verbs (yank, browser, the work-item actions, vote, palette) keep exactly their
/// existing handling.
/// </para>
/// </summary>
public sealed class ShellCommandRouter(
    WorkspaceViewModel workspace,
    Func<AppSection> activeSection,
    Func<bool> prListBuilt)
{
    // With CACHE-1 both list screens are always non-null once built, so the active section — not a
    // null check on the screen — decides which one a shell command targets.
    private bool WorkItemsActive => activeSection() == AppSection.WorkItems;
    private bool PullRequestsActive => activeSection() == AppSection.PullRequests;

    /// <summary>Routes one matched command. May mutate <see cref="WorkspaceViewModel"/> focus —
    /// that mutation is the decision.</summary>
    public ShellAction Route(AppCommand command, int? count)
    {
        // Workspace Tab (ADR 0024): with a visible preview, Tab cycles pane focus and is
        // consumed; while the preview is hidden the workspace declines it and Tab keeps
        // exactly today's semantics (the PR sub-tab intercept / section toggle below).
        if (command == AppCommand.CyclePane)
        {
            return workspace.CyclePane()
                ? new ShellAction(ShellActionKind.ApplyWorkspaceFocus, command, count)
                : Route(AppCommand.NextTab, count);
        }

        // C-h / C-l move workspace pane focus. When nothing changes (preview hidden, or
        // already at that edge) fall through so the keys keep their current behaviour.
        if (command is AppCommand.FocusLeft or AppCommand.FocusRight)
        {
            var changed = command == AppCommand.FocusLeft ? workspace.FocusLeft() : workspace.FocusRight();
            if (changed)
            {
                return new ShellAction(ShellActionKind.ApplyWorkspaceFocus, command, count);
            }
        }

        // In the PR section (with a built list), Tab/S-Tab cycle the PR sub-tabs (review
        // queue/team/mine/active) rather than switching top-level sections; section switches go
        // through the g-chords (gt/gT/g1/g2), handled by ShellViewModel.HandleCommand. When the PR
        // list isn't built (no connection → placeholder), decline so Tab still toggles sections.
        if (PullRequestsActive && prListBuilt() && command is AppCommand.NextTab or AppCommand.PrevTab)
        {
            var tab = command == AppCommand.NextTab ? ShellActionKind.PrNextTab : ShellActionKind.PrPrevTab;
            return new ShellAction(tab, command, count);
        }

        if (command.IsMovement())
        {
            return new ShellAction(MovementKind(command), command, count);
        }

        return new ShellAction(VerbKind(command), command, count);
    }

    /// <summary>
    /// Where a matched movement lands: the workspace decides preview-vs-list (ADR 0024 — without
    /// this a focused preview would be a trap that j/k cannot move), then the active section picks
    /// the list. Both screens are kept alive, so the section identifies the visible one.
    /// </summary>
    private ShellActionKind MovementKind(AppCommand command)
    {
        if (workspace.Route(command) == WorkspaceKeyRoute.PreviewScroll)
        {
            return ShellActionKind.ScrollPreview;
        }
        if (WorkItemsActive)
        {
            return ShellActionKind.NavigateWorkItemList;
        }
        return PullRequestsActive ? ShellActionKind.NavigatePrList : ShellActionKind.NavigateNothing;
    }

    /// <summary>The section-aware verbs. Everything else is the shell's, unrouted.</summary>
    private ShellActionKind VerbKind(AppCommand command) => command switch
    {
        // `r` forces a fresh load of the visible section only (CACHE-1 keeps the other's rows
        // as-is until it is next shown or refreshed).
        AppCommand.Refresh when WorkItemsActive => ShellActionKind.RefreshWorkItemList,
        AppCommand.Refresh when PullRequestsActive => ShellActionKind.RefreshPrList,
        AppCommand.Refresh => ShellActionKind.Consumed,

        // Only the work-item list has a filter prompt; `/` in the PR section has always been a
        // silent no-op, so it is consumed rather than declined (declining would surface a message).
        AppCommand.FilterStart when WorkItemsActive => ShellActionKind.StartWorkItemFilter,
        AppCommand.FilterStart => ShellActionKind.Consumed,

        AppCommand.Open when WorkItemsActive => ShellActionKind.OpenWorkItem,
        AppCommand.Open when PullRequestsActive => ShellActionKind.OpenPr,
        AppCommand.Open => ShellActionKind.Consumed,

        _ => ShellActionKind.NotRouted,
    };
}
