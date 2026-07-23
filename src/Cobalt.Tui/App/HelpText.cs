using System.Text;
using Cobalt.Core.Config;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.App;

/// <summary>Renders the `?` help overlay from the live binding table, so help never drifts from behavior.</summary>
public static class HelpText
{
    private static readonly Dictionary<AppCommand, string> Descriptions = new()
    {
        [AppCommand.MoveDown] = "move down",
        [AppCommand.MoveUp] = "move up",
        [AppCommand.MoveLeft] = "left / collapse",
        [AppCommand.MoveRight] = "right / expand",
        [AppCommand.MoveTop] = "jump to top",
        [AppCommand.MoveBottom] = "jump to bottom",
        [AppCommand.HalfPageDown] = "half page down",
        [AppCommand.HalfPageUp] = "half page up",
        [AppCommand.Open] = "open selection",
        [AppCommand.Back] = "quit (also :q)",
        [AppCommand.Refresh] = "refresh",
        [AppCommand.Help] = "this help",
        [AppCommand.CommandPalette] =
            $"command palette (:q, :context NAME, :scope, :done, :project NAME, :theme {string.Join('|', ThemeChoices.Names)}, "
            + $":preview {string.Join('|', PreviewModes.Names)})",
        [AppCommand.FilterStart] = "filter list",
        [AppCommand.NextTab] = "next tab",
        [AppCommand.PrevTab] = "previous tab",
        [AppCommand.FocusLeft] = "focus pane left",
        [AppCommand.FocusRight] = "focus pane right",
        [AppCommand.SectionWorkItems] = "work items section",
        [AppCommand.SectionPullRequests] = "pull requests section",
        [AppCommand.NextSection] = "next section",
        [AppCommand.PrevSection] = "previous section",
        [AppCommand.YankId] = "yank id/url",
        [AppCommand.OpenInBrowser] = "open in browser",
        [AppCommand.Comment] = "comment",
        [AppCommand.EditInEditor] = "edit in $EDITOR",
        [AppCommand.ChangeState] = "change state",
        [AppCommand.Assign] = "assign",
        [AppCommand.EditTags] = "edit tags",
        [AppCommand.Vote] = "vote on PR",
        [AppCommand.ResolveThread] = "resolve thread",
        [AppCommand.ReactivateThread] = "reactivate thread",
        [AppCommand.CompletePr] = "complete PR",
        [AppCommand.AbandonPr] = "abandon PR",
        [AppCommand.OpenDiff] = "open diff review",
        [AppCommand.NextFile] = "next file",
        [AppCommand.PrevFile] = "previous file",
        [AppCommand.CyclePane] = "switch file list / diff pane",
        [AppCommand.ToggleFold] = "collapse/expand folder",
        [AppCommand.ToggleDiffMode] = "unified / side-by-side diff",
        [AppCommand.NextHunk] = "next change hunk",
        [AppCommand.PrevHunk] = "previous change hunk",
        [AppCommand.NextThread] = "next comment thread",
        [AppCommand.PrevThread] = "previous comment thread",
        [AppCommand.NextUnviewedFile] = "next unviewed file",
        [AppCommand.PrevUnviewedFile] = "previous unviewed file",
        [AppCommand.SearchDiff] = "search in diff",
        [AppCommand.SearchNext] = "next match",
        [AppCommand.SearchPrev] = "previous match",
        [AppCommand.MarkViewed] = "mark file viewed",
        [AppCommand.MarkUnviewed] = "mark file unviewed",
        [AppCommand.ToggleThreadFilter] = "filter to unresolved threads",
        [AppCommand.ExpandContext] = "expand context",
        [AppCommand.ExpandAllContext] = "expand whole file",
        [AppCommand.AddPrComment] = "add PR comment",
        [AppCommand.ScrollLeft] = "scroll diff left",
        [AppCommand.ScrollRight] = "scroll diff right",
        [AppCommand.OpenBranch] = "open source branch in browser",
    };

    // The only global keys a modal dialog actually honors: the shared scroll seam plus
    // help and close. Everything else global (r, /, :, yy, gx, gt/gT, Tab, …) is dead in a
    // modal, so a dialog's `?` must not advertise it (M3).
    private static readonly HashSet<AppCommand> DialogGlobals =
    [
        AppCommand.MoveDown, AppCommand.MoveUp, AppCommand.MoveTop, AppCommand.MoveBottom,
        AppCommand.HalfPageDown, AppCommand.HalfPageUp, AppCommand.Help, AppCommand.Back,
    ];

    /// <summary>
    /// The shared one-line description for a command in <paramref name="scope"/> (the keybar's
    /// fallback vocabulary). Scope-aware because one command can mean different things per
    /// surface: <c>Tab</c> cycles list/preview inside the workspace and file-list/diff inside
    /// diff review.
    /// </summary>
    public static string Describe(AppCommand command, KeyScope scope) =>
        command == AppCommand.CyclePane && IsWorkspaceList(scope)
            ? "switch list / preview"
            : Descriptions.GetValueOrDefault(command, command.ToString().ToLowerInvariant());

    /// <summary>
    /// Full help for the main shell: every binding visible from the scope. <paramref name="previewVisible"/>
    /// is required, not defaulted — advertisement must never be able to drift from behaviour by
    /// a caller forgetting to say which state it is in.
    /// </summary>
    public static string For(KeyBindingTable table, KeyScope scope, bool previewVisible) =>
        Emit(table, scope, _ => true, previewVisible);

    /// <summary>
    /// Help for a modal dialog: only the verbs it dispatches — its scope's own bindings plus
    /// the shared scroll/help/close keys — so it never lists keys that do nothing (M3).
    /// </summary>
    public static string ForDialog(KeyBindingTable table, KeyScope scope)
    {
        var allowed = new HashSet<AppCommand>(DialogGlobals);
        foreach (var (_, command) in table.ScopedOnly(scope))
        {
            allowed.Add(command);
        }
        // A modal dialog has no workspace preview beside it; its own Tab (diff review) is
        // advertised with the diff wording, which the scope decides.
        return Emit(table, scope, allowed.Contains, previewVisible: false);
    }

    /// <summary>The two workspace list scopes, where Tab's meaning depends on the preview.</summary>
    private static bool IsWorkspaceList(KeyScope scope) =>
        scope is KeyScope.WorkItemList or KeyScope.PullRequestList;

    private static string Emit(KeyBindingTable table, KeyScope scope, Func<AppCommand, bool> include, bool previewVisible)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<AppCommand>();
        foreach (var (sequence, command) in table.Visible(scope))
        {
            // In the workspace list scopes Tab cycles list/preview focus only while the preview
            // shows; hidden, it falls back to today's NextTab semantics, so advertising
            // CyclePane there would drift from behaviour (#48).
            if (command == AppCommand.CyclePane && IsWorkspaceList(scope) && !previewVisible)
            {
                continue;
            }
            if (!include(command))
            {
                continue;
            }
            if (!seen.Add(command))
            {
                continue; // alias (e.g. Enter and o) — show the first binding only
            }
            var keys = string.Join("", sequence);
            sb.AppendLine($"  {keys,-8} {Describe(command, scope)}");
        }
        return sb.ToString();
    }
}
