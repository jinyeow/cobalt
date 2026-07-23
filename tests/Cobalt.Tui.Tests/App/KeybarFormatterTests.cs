using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The always-visible bottom keybar (ADR 0021).
/// Pure formatting from the live binding table: prioritized entries, fitted to
/// width, always ending with the help key so `?` is discoverable even truncated.
/// </summary>
public class KeybarFormatterTests
{
    [Fact]
    public void WorkItem_Scope_Shows_Movement_Verbs_And_Help()
    {
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.WorkItemList, 200, previewVisible: false);

        Assert.Contains("j/k:move", bar);
        Assert.Contains("o:open", bar);
        Assert.Contains("c:comment", bar);
        Assert.Contains("s:state", bar);
        Assert.Contains("a:assign", bar);
        Assert.Contains("t:tags", bar);
        Assert.EndsWith("?:help", bar);
    }

    [Fact]
    public void Pr_Scope_Shows_Vote_But_Not_WorkItem_Verbs()
    {
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.PullRequestList, 200, previewVisible: false);

        Assert.Contains("v:vote", bar);
        Assert.DoesNotContain("a:assign", bar);
        Assert.DoesNotContain("t:tags", bar);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(20)]
    public void Fits_The_Width_And_Never_Wraps(int width)
    {
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.WorkItemList, width, previewVisible: false);

        Assert.True(bar.Length <= width, $"keybar length {bar.Length} exceeds width {width}");
        Assert.DoesNotContain('\n', bar);
    }

    [Fact]
    public void Truncated_Keybar_Still_Ends_With_Help()
    {
        // Too narrow for everything, wide enough for a few entries + help.
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.WorkItemList, 30, previewVisible: false);

        Assert.EndsWith("?:help", bar);
        Assert.True(bar.Length <= 30);
    }

    [Fact]
    public void Priority_Entries_Come_Before_The_Rest()
    {
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.WorkItemList, 400, previewVisible: false);

        // The curated verbs must render before low-priority table-order extras
        // (e.g. yank/browser), so a narrow bar keeps the valuable keys.
        Assert.True(bar.IndexOf("c:comment", StringComparison.Ordinal) < bar.IndexOf("yy:yank", StringComparison.Ordinal));
    }

    [Fact]
    public void A_New_Binding_Appears_Without_Touching_The_Formatter()
    {
        // A command with no curated keybar label still shows up, described from the
        // shared help vocabulary — the keybar can never drift from the table.
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "j", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "?", AppCommand.Help);
        table.Bind(KeyScope.WorkItemList, "Q", AppCommand.MarkViewed);

        var bar = KeybarFormatter.Render(table, KeyScope.WorkItemList, 200, previewVisible: false);

        Assert.Contains("Q:mark file viewed", bar);
    }

    [Fact]
    public void Movement_Pair_Collapses_To_One_Entry()
    {
        var table = new KeyBindingTable();
        table.Bind(KeyScope.Global, "j", AppCommand.MoveDown);
        table.Bind(KeyScope.Global, "?", AppCommand.Help);

        // Only MoveDown bound → single-key movement entry, no "/".
        var bar = KeybarFormatter.Render(table, KeyScope.Global, 200, previewVisible: false);

        Assert.Contains("j:move", bar);
        Assert.DoesNotContain("j/", bar);
    }

    [Fact]
    public void Tiny_Width_Does_Not_Throw_Or_Overflow()
    {
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.WorkItemList, 5, previewVisible: false);

        Assert.True(bar.Length <= 5);
    }

    [Theory]
    [InlineData(KeyScope.WorkItemList)]
    [InlineData(KeyScope.PullRequestList)]
    public void Workspace_List_Keybar_Is_Byte_Identical_To_Pre_M5_When_The_Preview_Is_Hidden(KeyScope scope)
    {
        // Same rule as the help overlay: with the preview hidden (collapsed by width, or
        // `preview = off`) Tab in the list scopes still performs today's tab cycling — the
        // shell's fallback — so the keybar must stay byte-for-byte what it rendered pre-M5.
        // Reference = the default table with Tab→CyclePane unbound from both workspace list
        // scopes (config empty-sequence unbind, independent of the render-time suppression).
        // Width 400 so nothing truncates.
        var expected = KeybarFormatter.Render(WithoutWorkspaceTabCyclePane(), scope, 400, previewVisible: false);

        Assert.Equal(expected, KeybarFormatter.Render(KeyBindingTable.Default(), scope, 400, previewVisible: false));
    }

    [Theory]
    [InlineData(KeyScope.WorkItemList)]
    [InlineData(KeyScope.PullRequestList)]
    public void Workspace_List_Keybar_Advertises_Tab_With_Workspace_Wording_When_The_Preview_Shows(KeyScope scope)
    {
        // The mirror of the pin above (#48): a visible preview makes Tab a real pane-focus
        // cycle, so the bar advertises it — in the workspace's wording, not diff review's.
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), scope, 400, previewVisible: true);

        Assert.Contains("Tab:switch list / preview", bar);
        Assert.DoesNotContain("switch file list / diff pane", bar);
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
    public void DiffReview_Keybar_Still_Advertises_Pane_Cycling()
    {
        // Regression pin (green before and after the workspace suppression): diff review's
        // own scoped Tab → CyclePane stays advertised — the suppression is workspace-only.
        // Width 700: CyclePane sits late in bind order, so a narrower bar truncates first.
        var bar = KeybarFormatter.Render(KeyBindingTable.Default(), KeyScope.DiffReview, 700, previewVisible: false);

        Assert.Contains("Tab:switch file list / diff pane", bar);
    }

    [Fact]
    public void A_Remapped_Table_Renders_The_New_Key_Without_Formatter_Changes()
    {
        // A user config remap (move-down -> "n") must surface in the keybar with zero
        // changes to KeybarFormatter — it renders from the live table (ticket #30).
        var commands = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["move-down"] = ["n"] };
        var scopes = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase) { ["global"] = commands };
        var table = KeyBindingTable.FromConfig(new KeysConfig(scopes));

        var bar = KeybarFormatter.Render(table, KeyScope.WorkItemList, 200, previewVisible: false);

        Assert.Contains("n/k:move", bar);
        Assert.DoesNotContain("j/k:move", bar);
    }
}
