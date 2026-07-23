using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// A modal dialog's `?` overlay must advertise only the keys it actually dispatches —
/// no dead global keys (r, /, yy, gt/gT) and no verbs from other scopes (M3).
/// </summary>
public class HelpTextTests
{
    private static readonly KeyBindingTable Table = KeyBindingTable.Default();

    [Fact]
    public void DiffReview_Help_Omits_Resolve_Reactivate_But_Includes_Vote()
    {
        var help = HelpText.ForDialog(Table, KeyScope.DiffReview);

        // Resolve/reactivate belong to PR detail, not diff review — the dialog
        // dispatches neither. Vote, however, is now bound in diff review too.
        // ("resolve thread", not the bare substring, to avoid a false match against
        // ToggleThreadFilter's "unresolved threads" description.)
        Assert.DoesNotContain("resolve thread", help);
        Assert.DoesNotContain("reactivate", help);
        Assert.Contains("vote", help);
        // Its real verbs are present.
        Assert.Contains("comment", help);
        Assert.Contains("next file", help);
        Assert.Contains("switch file list / diff pane", help);
    }

    [Fact]
    public void Dialog_Help_Omits_Dead_Global_Keys()
    {
        var help = HelpText.ForDialog(Table, KeyScope.WorkItemDetail);

        // Global keys that do nothing inside a modal must not be advertised.
        Assert.DoesNotContain("refresh", help);       // r
        Assert.DoesNotContain("yank", help);          // yy
        Assert.DoesNotContain("next section", help);  // gt
        Assert.DoesNotContain("filter list", help);   // /
        // But the shared scroll + close/help keys and the dialog's own verbs stay.
        Assert.Contains("move down", help);
        Assert.Contains("change state", help);
        Assert.Contains("this help", help);
    }

    [Fact]
    public void Shell_Help_Still_Lists_The_Full_Global_Table()
    {
        // The main-shell `?` is unchanged — it still shows refresh/filter/section keys.
        var help = HelpText.For(Table, KeyScope.WorkItemList);

        Assert.Contains("refresh", help);
        Assert.Contains("filter list", help);
        Assert.Contains("next section", help);
    }

    [Fact]
    public void Workspace_List_Help_Advertises_Todays_Tab_Semantics_Not_CyclePane()
    {
        // M5: Tab is bound to CyclePane in the two workspace list scopes, but while the
        // preview is hidden the shell falls back to today's NextTab semantics — and at M5
        // the preview is never visible. Advertising CyclePane (with diff-review wording)
        // would be exactly the advertised-but-dead drift this surface must never show, so
        // the help stays byte-identical to pre-M5 until #48 makes the preview visible.
        var wiHelp = HelpText.For(Table, KeyScope.WorkItemList);
        Assert.DoesNotContain("switch file list / diff pane", wiHelp);
        Assert.Contains("Tab      next tab", wiHelp);
        Assert.Equal(1, wiHelp.Split('\n').Count(l => l.TrimStart().StartsWith("Tab ", StringComparison.Ordinal)));

        var prHelp = HelpText.For(Table, KeyScope.PullRequestList);
        Assert.DoesNotContain("switch file list / diff pane", prHelp);
        Assert.Contains("]        next tab", prHelp); // [ / ] stay the canonical sub-tab keys
        Assert.DoesNotContain(prHelp.Split('\n'), l => l.TrimStart().StartsWith("Tab ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Remapped_Table_Renders_The_New_Key_In_Help_Without_Formatter_Changes()
    {
        // Same guarantee as the keybar: `?` help derives from the live table, so a config
        // remap (move-down -> "n") surfaces automatically (ticket #30).
        var commands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["move-down"] = ["n"] };
        var scopes = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase) { ["global"] = commands };
        var table = KeyBindingTable.FromConfig(new KeysConfig(scopes));

        var help = HelpText.For(table, KeyScope.WorkItemList);

        Assert.Contains("n        move down", help);
        Assert.DoesNotContain("j        move down", help);
    }
}
