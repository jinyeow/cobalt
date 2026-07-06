using System.Text;
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
        [AppCommand.CommandPalette] = "command palette (:q quit, :context NAME)",
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
    };

    public static string For(KeyBindingTable table, KeyScope scope)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<AppCommand>();
        foreach (var (sequence, command) in table.Visible(scope))
        {
            if (!seen.Add(command))
            {
                continue; // alias (e.g. Enter and o) — show the first binding only
            }
            var keys = string.Join("", sequence);
            sb.AppendLine($"  {keys,-8} {Descriptions.GetValueOrDefault(command, command.ToString())}");
        }
        return sb.ToString();
    }
}
