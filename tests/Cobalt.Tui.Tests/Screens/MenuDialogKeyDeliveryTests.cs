using System.Drawing;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: builds the menu popup and drives keys through the real Terminal.Gui
/// routing. The menu's whole grammar (j/k with counts, gg/G, Enter executes, Esc/q dismisses,
/// every other matched global swallowed) lives here because key delivery is the one part a
/// view-model test cannot prove.
/// </summary>
public class MenuDialogKeyDeliveryTests
{
    private static readonly IApplication App = Application.Create();

    private static IReadOnlyList<MenuOption<AppCommand>> Options() =>
    [
        new MenuOption<AppCommand>("move down", "j", AppCommand.MoveDown),
        new MenuOption<AppCommand>("refresh", "r", AppCommand.Refresh),
        new MenuOption<AppCommand>("open selection", "Enter", AppCommand.Open),
        new MenuOption<AppCommand>("this help", "?", AppCommand.Help),
        new MenuOption<AppCommand>("yank id/url", "yy", AppCommand.YankId),
    ];

    /// <summary>A laid-out, focused menu plus the seams a test asserts on.</summary>
    private sealed class Harness
    {
        public required MenuViewModel<AppCommand> Vm { get; init; }
        public required Dialog Dialog { get; init; }
        public required ListView List { get; init; }
        public required TextField Filter { get; init; }
        public MenuOption<AppCommand>? Accepted { get; set; }
        public int Closes { get; set; }
    }

    private static Harness NewMenu()
    {
        var vm = new MenuViewModel<AppCommand>(Options());
        Harness? harness = null;
        var dialog = MenuDialog.Build(
            App, "keys", vm, KeyBindingTable.Shared,
            onAccept: option => harness!.Accepted = option,
            requestClose: () => harness!.Closes++,
            out var list, out var filter);
        harness = new Harness { Vm = vm, Dialog = dialog, List = list, Filter = filter };
        dialog.Layout(new Size(60, 12));
        dialog.SetFocus();
        return harness;
    }

    /// <summary>The text actually bound to a row — what the user reads, not just how many rows exist.</summary>
    private static string RowText(ListView list, int index) =>
        list.Source?.ToList()[index]?.ToString() ?? "";

    [Fact]
    public void The_Rows_Carry_Their_Hint_And_Label_After_The_First_Layout()
    {
        // The row texts are width-dependent, so they are (re)formatted from a layout event. If
        // that event's width is read one pass behind, the menu opens blank and only fills in on a
        // second layout — a user pressing '?' would see an empty popup.
        var menu = NewMenu();

        Assert.Equal("  j        move down", RowText(menu.List, 0));
        Assert.Equal("  yy       yank id/url", RowText(menu.List, 4));
    }

    [Fact]
    public void The_Menu_Binds_Every_Row_And_Starts_On_The_First()
    {
        var menu = NewMenu();

        Assert.Equal(5, menu.List.Source?.Count);
        Assert.Equal(0, menu.List.SelectedItem);
        Assert.Equal(AppCommand.MoveDown, menu.Vm.Selected?.Value);
    }

    [Fact]
    public void J_And_K_Move_The_Selection()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('j'));
        menu.Dialog.NewKeyDownEvent(new Key('j'));
        Assert.Equal(AppCommand.Open, menu.Vm.Selected?.Value);

        menu.Dialog.NewKeyDownEvent(new Key('k'));
        Assert.Equal(AppCommand.Refresh, menu.Vm.Selected?.Value);
    }

    [Fact]
    public void A_Count_Prefix_Moves_By_That_Many_Rows()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('3'));
        menu.Dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(3, menu.List.SelectedItem);
        Assert.Equal(AppCommand.Help, menu.Vm.Selected?.Value);
    }

    [Fact]
    public void G_Jumps_To_The_Last_Row_And_gg_Back_To_The_First()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('G'));
        Assert.Equal(AppCommand.YankId, menu.Vm.Selected?.Value);

        menu.Dialog.NewKeyDownEvent(new Key('g'));
        menu.Dialog.NewKeyDownEvent(new Key('g'));
        Assert.Equal(AppCommand.MoveDown, menu.Vm.Selected?.Value);
    }

    [Fact]
    public void Enter_Accepts_The_Selected_Row_And_Closes()
    {
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('j'));

        menu.Dialog.NewKeyDownEvent(Key.Enter);

        Assert.Equal(AppCommand.Refresh, menu.Accepted?.Value);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void A_Selection_Moved_By_A_Native_Key_Still_Executes_The_Highlighted_Row()
    {
        // End/PageDown carry no vim token, so the router stands down and the ListView moves its
        // own highlight. Enter must run the row the user is looking at, not the last one the
        // router happened to mirror.
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(Key.End);
        var highlighted = menu.List.SelectedItem;
        menu.Dialog.NewKeyDownEvent(Key.Enter);

        Assert.Equal(4, highlighted);
        Assert.Equal(AppCommand.YankId, menu.Accepted?.Value);
    }

    [Fact]
    public void Esc_Closes_Without_Accepting_Anything()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(Key.Esc);

        Assert.Null(menu.Accepted);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void Q_Dismisses_The_Menu()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('q'));

        Assert.Null(menu.Accepted);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void Esc_After_A_Pending_Sequence_Only_Clears_It()
    {
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('g')); // pending 'g g'
        menu.Dialog.NewKeyDownEvent(Key.Esc);

        Assert.Equal(0, menu.Closes);
    }

    [Fact]
    public void A_Matched_But_Irrelevant_Global_Is_Swallowed_And_The_Menu_Stays_Open()
    {
        var menu = NewMenu();

        // The menu has no native widget behaviour worth falling through to, and letting '?'
        // through would recurse into another menu.
        menu.Dialog.NewKeyDownEvent(new Key('r'));
        menu.Dialog.NewKeyDownEvent(new Key('?'));

        Assert.Null(menu.Accepted);
        Assert.Equal(0, menu.Closes);
    }

    [Fact]
    public void An_Irrelevant_Global_Is_Consumed_So_It_Cannot_Reach_The_Shell()
    {
        // "Nothing happened here" is not enough: an unhandled key bubbles on to the shell, where
        // '?' would open a second menu on top of this one.
        var menu = NewMenu();

        Assert.True(menu.Dialog.NewKeyDownEvent(new Key('?')));
        Assert.True(menu.Dialog.NewKeyDownEvent(new Key('r')));
    }

    [Fact]
    public void The_Menus_Letters_Reach_The_Router_Not_A_Type_Ahead_Search()
    {
        // A ListView's CollectionNavigator would consume 'r' as a search prefix and 'q' as its
        // continuation, so the dismiss key would never reach the router (the PrListView lesson).
        var menu = NewMenu();

        menu.Dialog.NewKeyDownEvent(new Key('r'));
        menu.Dialog.NewKeyDownEvent(new Key('q'));

        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void Type_Ahead_Does_Not_Steal_The_Menus_Letters()
    {
        // A ListView's CollectionNavigator would otherwise consume 'r'/'q' as a search prefix
        // before the router sees them (the PrListView lesson).
        var menu = NewMenu();

        Assert.Null(menu.List.KeystrokeNavigator);
    }

    // ---- The filter bar (#20: "/ filters rows incrementally; filtered Enter still executes") ----

    [Fact]
    public void The_Filter_Bar_Is_Hidden_Until_Slash()
    {
        var menu = NewMenu();

        Assert.False(menu.Filter.Visible);

        menu.Dialog.NewKeyDownEvent(new Key('/'));

        Assert.True(menu.Filter.Visible);
        Assert.True(menu.Filter.HasFocus);
    }

    [Fact]
    public void Typing_In_The_Filter_Bar_Narrows_The_Bound_Rows()
    {
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('/'));

        menu.Dialog.NewKeyDownEvent(new Key('r'));

        // "r refresh", "Enter open selection" and "yy yank id/url" all carry an 'r'; the
        // movement and help rows do not — and the survivors keep their original order.
        Assert.Equal("r", menu.Filter.Text);
        Assert.Equal(
            [AppCommand.Refresh, AppCommand.Open, AppCommand.YankId],
            menu.Vm.VisibleOptions.Select(o => o.Value));
        Assert.Equal(3, menu.List.Source?.Count);
    }

    [Fact]
    public void Enter_While_Filtering_Executes_The_Selected_Row()
    {
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('/'));
        menu.Dialog.NewKeyDownEvent(new Key('r'));

        menu.Dialog.NewKeyDownEvent(Key.Enter);

        Assert.Equal(AppCommand.Refresh, menu.Accepted?.Value);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void Esc_In_The_Filter_Bar_Clears_It_And_Leaves_The_Menu_Open()
    {
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('/'));
        menu.Dialog.NewKeyDownEvent(new Key('r'));

        menu.Dialog.NewKeyDownEvent(Key.Esc);

        Assert.Equal(0, menu.Closes);
        Assert.False(menu.Filter.Visible);
        Assert.Equal("", menu.Vm.Filter);
        Assert.Equal(5, menu.List.Source?.Count);
        Assert.True(menu.List.HasFocus);

        // Only the next Esc dismisses the menu itself.
        menu.Dialog.NewKeyDownEvent(Key.Esc);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void A_Filter_That_Matches_Nothing_Shows_The_Placeholder_And_Runs_Nothing()
    {
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('/'));

        menu.Dialog.NewKeyDownEvent(new Key('z'));

        Assert.Empty(menu.Vm.VisibleOptions);
        Assert.Equal("no matches", RowText(menu.List, 0));
        Assert.Equal(0, menu.Closes);

        // Enter on nothing dismisses without choosing (ADR 0025) — one Open path, no crash.
        menu.Dialog.NewKeyDownEvent(Key.Enter);
        Assert.Null(menu.Accepted);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void Focus_Leaving_The_Filter_Bar_Cancels_The_Filter_Instead_Of_Orphaning_It()
    {
        // Focus can reach the list without the menu's own cancel path running — a click on a row
        // takes it (Terminal.Gui focuses a CanFocus view on press). A bar left visible with stale
        // text keeps narrowing the rows, and the next Esc closes the menu instead of clearing the
        // filter, contradicting ADR 0025's "a second Esc closes the menu".
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('/'));
        menu.Dialog.NewKeyDownEvent(new Key('r'));

        menu.List.SetFocus();

        Assert.False(menu.Filter.HasFocus);
        Assert.False(menu.Filter.Visible);
        Assert.Equal("", menu.Vm.Filter);
        Assert.Equal(5, menu.List.Source?.Count);

        // And the one Esc the user has left dismisses the menu, as the ADR says.
        menu.Dialog.NewKeyDownEvent(Key.Esc);
        Assert.Equal(1, menu.Closes);
    }

    [Fact]
    public void CtrlU_While_Filtering_Does_Not_Scroll_The_List_Underneath()
    {
        // The ADR 0014 search-bar guard: control chords bubble past the field to the dialog, so
        // the dialog-level router must stand down while the field owns the keys.
        var menu = NewMenu();
        menu.Dialog.NewKeyDownEvent(new Key('G')); // selection on the last row
        menu.Dialog.NewKeyDownEvent(new Key('/'));

        menu.Dialog.NewKeyDownEvent(new Key('u').WithCtrl);

        Assert.Equal(4, menu.List.SelectedItem);
    }
}
