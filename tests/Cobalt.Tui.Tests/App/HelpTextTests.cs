using System.Text;
using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

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

    // ---- The executable menu shares one row builder with the string help (#20) ----

    [Theory]
    [InlineData(KeyScope.WorkItemList, false)]
    [InlineData(KeyScope.WorkItemList, true)]
    [InlineData(KeyScope.PullRequestList, true)]
    [InlineData(KeyScope.DiffReview, false)]
    public void Shell_Menu_Rows_Re_Emit_The_String_Help_Byte_For_Byte(KeyScope scope, bool previewVisible)
    {
        // One suppression engine for both surfaces: whatever the string help advertises, the
        // menu offers, in the same order and with the same wording.
        var rows = HelpText.MenuFor(Table, scope, previewVisible);

        Assert.Equal(HelpText.For(Table, scope, previewVisible), ReEmit(rows));
    }

    [Fact]
    public void Dialog_Menu_Rows_Re_Emit_The_Dialog_String_Help_Byte_For_Byte()
    {
        var rows = HelpText.MenuForDialog(Table, KeyScope.WorkItemDetail);

        Assert.Equal(HelpText.ForDialog(Table, KeyScope.WorkItemDetail), ReEmit(rows));
    }

    [Fact]
    public void Dialog_Menu_Rows_Carry_The_Key_Hint_And_Description_And_No_Dead_Global()
    {
        var rows = HelpText.MenuForDialog(Table, KeyScope.WorkItemDetail);

        Assert.Contains(rows, r => r is { KeyHint: "s", Label: "change state", Value: AppCommand.ChangeState });
        Assert.Contains(rows, r => r is { KeyHint: "j", Label: "move down", Value: AppCommand.MoveDown });
        // The M3 suppression applies to the rows themselves, not just their rendering: a menu
        // must never offer a verb the dialog does not dispatch.
        Assert.DoesNotContain(rows, r => r.Value == AppCommand.Refresh);
        Assert.DoesNotContain(rows, r => r.Value == AppCommand.YankId);
    }

    [Fact]
    public void Menu_Rows_Collapse_Aliases_To_The_First_Binding()
    {
        // Enter/o/l all open; the menu shows one executable row for the command, as the string
        // cheatsheet always has.
        var rows = HelpText.MenuFor(Table, KeyScope.WorkItemList, previewVisible: false);

        var open = Assert.Single(rows, r => r.Value == AppCommand.Open);
        Assert.Equal("Enter", open.KeyHint);
    }

    // ---- Golden pins: the exact overlay text as of the pre-menu implementation (00916f1) ----

    /// <summary>
    /// The cheatsheet's rows exactly as the pre-menu renderer emitted them, transcribed from that
    /// implementation's own output rather than re-derived from today's builder — so a change in
    /// the row builder moves only one side of the comparison and the pin actually fails.
    /// </summary>
    private static readonly string[] WorkItemListPreviewHidden =
    [
        "  c        comment",
        "  s        change state",
        "  a        assign",
        "  t        edit tags",
        "  Backspace focus pane left",
        "  j        move down",
        "  k        move up",
        "  gg       jump to top",
        "  G        jump to bottom",
        "  C-d      half page down",
        "  C-u      half page up",
        "  Enter    open selection",
        "  q        quit (also :q)",
        "  r        refresh",
        "  ?        this help",
        "  :        command palette (:q, :context NAME, :scope, :done, :project NAME, :theme dark|light|system, :preview auto|off)",
        "  /        filter list",
        "  Tab      next tab",
        "  S-Tab    previous tab",
        "  C-l      focus pane right",
        "  gt       next section",
        "  gT       previous section",
        "  g1       work items section",
        "  g2       pull requests section",
        "  yy       yank id/url",
        "  gx       open in browser",
    ];

    private static readonly string[] WorkItemListPreviewShown =
    [
        "  c        comment",
        "  s        change state",
        "  a        assign",
        "  t        edit tags",
        "  Tab      switch list / preview",
        "  Backspace focus pane left",
        "  j        move down",
        "  k        move up",
        "  gg       jump to top",
        "  G        jump to bottom",
        "  C-d      half page down",
        "  C-u      half page up",
        "  Enter    open selection",
        "  q        quit (also :q)",
        "  r        refresh",
        "  ?        this help",
        "  :        command palette (:q, :context NAME, :scope, :done, :project NAME, :theme dark|light|system, :preview auto|off)",
        "  /        filter list",
        "  Tab      next tab",
        "  S-Tab    previous tab",
        "  C-l      focus pane right",
        "  gt       next section",
        "  gT       previous section",
        "  g1       work items section",
        "  g2       pull requests section",
        "  yy       yank id/url",
        "  gx       open in browser",
    ];

    private static readonly string[] PullRequestListPreviewHidden =
    [
        "  v        vote on PR",
        "  ]        next tab",
        "  [        previous tab",
        "  Backspace focus pane left",
        "  j        move down",
        "  k        move up",
        "  gg       jump to top",
        "  G        jump to bottom",
        "  C-d      half page down",
        "  C-u      half page up",
        "  Enter    open selection",
        "  q        quit (also :q)",
        "  r        refresh",
        "  ?        this help",
        "  :        command palette (:q, :context NAME, :scope, :done, :project NAME, :theme dark|light|system, :preview auto|off)",
        "  /        filter list",
        "  C-l      focus pane right",
        "  gt       next section",
        "  gT       previous section",
        "  g1       work items section",
        "  g2       pull requests section",
        "  yy       yank id/url",
        "  gx       open in browser",
    ];

    private static readonly string[] PullRequestListPreviewShown =
    [
        "  v        vote on PR",
        "  ]        next tab",
        "  [        previous tab",
        "  Tab      switch list / preview",
        "  Backspace focus pane left",
        "  j        move down",
        "  k        move up",
        "  gg       jump to top",
        "  G        jump to bottom",
        "  C-d      half page down",
        "  C-u      half page up",
        "  Enter    open selection",
        "  q        quit (also :q)",
        "  r        refresh",
        "  ?        this help",
        "  :        command palette (:q, :context NAME, :scope, :done, :project NAME, :theme dark|light|system, :preview auto|off)",
        "  /        filter list",
        "  C-l      focus pane right",
        "  gt       next section",
        "  gT       previous section",
        "  g1       work items section",
        "  g2       pull requests section",
        "  yy       yank id/url",
        "  gx       open in browser",
    ];

    private static readonly string[] WorkItemDetailDialog =
    [
        "  c        comment",
        "  e        edit in $EDITOR",
        "  s        change state",
        "  a        assign",
        "  t        edit tags",
        "  j        move down",
        "  k        move up",
        "  gg       jump to top",
        "  G        jump to bottom",
        "  C-d      half page down",
        "  C-u      half page up",
        "  q        quit (also :q)",
        "  ?        this help",
    ];
    /// <summary>The overlay's line separator is the platform's, so join rather than hardcode it.</summary>
    private static string Overlay(string[] rows) => string.Join(Environment.NewLine, rows) + Environment.NewLine;

    [Fact]
    public void Work_Item_List_Help_Still_Renders_The_Pre_Menu_Overlay()
    {
        Assert.Equal(Overlay(WorkItemListPreviewHidden), HelpText.For(Table, KeyScope.WorkItemList, previewVisible: false));
        Assert.Equal(Overlay(WorkItemListPreviewShown), HelpText.For(Table, KeyScope.WorkItemList, previewVisible: true));
    }

    [Fact]
    public void Pull_Request_List_Help_Still_Renders_The_Pre_Menu_Overlay()
    {
        Assert.Equal(Overlay(PullRequestListPreviewHidden), HelpText.For(Table, KeyScope.PullRequestList, previewVisible: false));
        Assert.Equal(Overlay(PullRequestListPreviewShown), HelpText.For(Table, KeyScope.PullRequestList, previewVisible: true));
    }

    [Fact]
    public void Dialog_Help_Still_Renders_The_Pre_Menu_Overlay()
    {
        Assert.Equal(Overlay(WorkItemDetailDialog), HelpText.ForDialog(Table, KeyScope.WorkItemDetail));
    }

    /// <summary>The cheatsheet's row format, spelled out here independently of the production
    /// renderer so the byte-identical pins above compare against a real second opinion.</summary>
    private static string ReEmit(IReadOnlyList<MenuOption<AppCommand>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine($"  {row.KeyHint,-8} {row.Label}");
        }
        return sb.ToString();
    }
}
