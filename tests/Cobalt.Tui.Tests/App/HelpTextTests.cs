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
    public void DiffReview_Help_Omits_Resolve_Reactivate_And_Vote()
    {
        var help = HelpText.ForDialog(Table, KeyScope.DiffReview);

        // These belong to PR detail, not diff review — the dialog dispatches none of them.
        Assert.DoesNotContain("resolve", help);
        Assert.DoesNotContain("reactivate", help);
        Assert.DoesNotContain("vote", help);
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
}
