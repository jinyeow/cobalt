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
        var help = HelpText.For(Table, KeyScope.WorkItemList, previewVisible: false);

        Assert.Contains("refresh", help);
        Assert.Contains("filter list", help);
        Assert.Contains("next section", help);
    }

    [Theory]
    [InlineData(KeyScope.WorkItemList)]
    [InlineData(KeyScope.PullRequestList)]
    public void Workspace_List_Help_Is_Byte_Identical_To_Pre_M5_When_The_Preview_Is_Hidden(KeyScope scope)
    {
        // M5 binds Tab→CyclePane in the two workspace list scopes, but with the preview hidden
        // (collapsed by width, or `preview = off`) the shell falls back to today's NextTab
        // semantics — so the `?` overlay must stay byte-for-byte what it rendered pre-M5.
        // Reference = the default table with that one binding unbound via the config
        // empty-sequence path (independent of the render-time suppression), i.e. the table as if
        // M5 had never added it; the live default must render identically.
        var expected = HelpText.For(WithoutWorkspaceTabCyclePane(), scope, previewVisible: false);

        Assert.Equal(expected, HelpText.For(Table, scope, previewVisible: false));
    }

    [Theory]
    [InlineData(KeyScope.WorkItemList)]
    [InlineData(KeyScope.PullRequestList)]
    public void Workspace_List_Help_Advertises_Tab_With_Workspace_Wording_When_The_Preview_Shows(KeyScope scope)
    {
        // The mirror of the pin above (#48): once the preview is visible Tab really does cycle
        // pane focus, so help must say so — and in the workspace's own words, not diff review's.
        var help = HelpText.For(Table, scope, previewVisible: true);

        Assert.Contains("Tab      switch list / preview", help);
        Assert.DoesNotContain("switch file list / diff pane", help);
    }

    /// <summary>The default table with Tab→CyclePane unbound from both workspace list scopes
    /// (config empty-sequence unbind) — a pre-M5 reference built without copying Default()'s binds.</summary>
    private static KeyBindingTable WithoutWorkspaceTabCyclePane()
    {
        static IReadOnlyDictionary<string, IReadOnlyList<string>> Unbind() =>
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["cycle-pane"] = [] };
        var scopes = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["workitemlist"] = Unbind(),
            ["pullrequestlist"] = Unbind(),
        };
        return KeyBindingTable.FromConfig(new KeysConfig(scopes));
    }

    [Fact]
    public void A_Remapped_Table_Renders_The_New_Key_In_Help_Without_Formatter_Changes()
    {
        // Same guarantee as the keybar: `?` help derives from the live table, so a config
        // remap (move-down -> "n") surfaces automatically (ticket #30).
        var commands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["move-down"] = ["n"] };
        var scopes = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase) { ["global"] = commands };
        var table = KeyBindingTable.FromConfig(new KeysConfig(scopes));

        var help = HelpText.For(table, KeyScope.WorkItemList, previewVisible: false);

        Assert.Contains("n        move down", help);
        Assert.DoesNotContain("j        move down", help);
    }
}
