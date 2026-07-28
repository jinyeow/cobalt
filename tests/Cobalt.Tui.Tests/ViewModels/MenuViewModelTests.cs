using Cobalt.Tui.Input;
using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

/// <summary>
/// The menu component's state (ADR 0022 stage C): a fixed row list, an incremental
/// order-preserving filter, a clamped selection, and the rendered row texts. Terminal.Gui-free,
/// so the whole of the menu's behaviour is provable without a terminal.
/// </summary>
public class MenuViewModelTests
{
    private static MenuViewModel<AppCommand> Vm() => new(
    [
        new MenuOption<AppCommand>("move down", "j", AppCommand.MoveDown),
        new MenuOption<AppCommand>("refresh", "r", AppCommand.Refresh),
        new MenuOption<AppCommand>("open selection", "Enter", AppCommand.Open),
        new MenuOption<AppCommand>("this help", "?", AppCommand.Help),
    ]);

    [Fact]
    public void All_Options_Are_Visible_Before_Any_Filter()
    {
        var vm = Vm();

        Assert.Equal(
            [AppCommand.MoveDown, AppCommand.Refresh, AppCommand.Open, AppCommand.Help],
            vm.VisibleOptions.Select(o => o.Value));
        Assert.Equal("", vm.Filter);
    }

    [Fact]
    public void Filter_Narrows_Incrementally_And_Preserves_The_Original_Order()
    {
        var vm = Vm();

        vm.SetFilter("e");
        // Order is the row order, never a rank reorder: help hints must not jump while typing.
        // (The palette's prefix-first ranking would have floated the "Enter" row to the top.)
        Assert.Equal(
            [AppCommand.MoveDown, AppCommand.Refresh, AppCommand.Open, AppCommand.Help],
            vm.VisibleOptions.Select(o => o.Value));

        vm.SetFilter("en");
        Assert.Equal(
            [AppCommand.MoveDown, AppCommand.Open],
            vm.VisibleOptions.Select(o => o.Value));
        Assert.Equal("en", vm.Filter);
    }

    [Fact]
    public void Filter_Matches_The_Key_Hint_Too_Not_Just_The_Label()
    {
        var vm = Vm();

        // "?" appears only in the Help row's key hint.
        vm.SetFilter("?");

        Assert.Equal([AppCommand.Help], vm.VisibleOptions.Select(o => o.Value));
    }

    [Fact]
    public void Filter_Is_Case_Insensitive()
    {
        var vm = Vm();

        vm.SetFilter("REF");

        Assert.Equal([AppCommand.Refresh], vm.VisibleOptions.Select(o => o.Value));
    }

    [Fact]
    public void Setting_A_Filter_Resets_The_Selection_To_The_Top_Match()
    {
        var vm = Vm();
        vm.SelectedIndex = 2;

        vm.SetFilter("e");

        Assert.Equal(0, vm.SelectedIndex);
        Assert.Equal(AppCommand.MoveDown, vm.Selected?.Value);
    }

    [Fact]
    public void Selection_Is_Clamped_To_The_Visible_Rows()
    {
        var vm = Vm();

        vm.SelectedIndex = 99;
        Assert.Equal(3, vm.SelectedIndex);

        vm.SelectedIndex = -4;
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void Selection_Moves_Within_The_Filtered_Rows()
    {
        var vm = Vm();
        vm.SetFilter("en");

        vm.SelectedIndex = 1;

        Assert.Equal(AppCommand.Open, vm.Selected?.Value);
    }

    [Fact]
    public void A_Filter_That_Matches_Nothing_Leaves_No_Selection()
    {
        var vm = Vm();

        vm.SetFilter("zzz");

        Assert.Empty(vm.VisibleOptions);
        Assert.Null(vm.Selected);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void Clearing_The_Filter_Restores_Every_Row()
    {
        var vm = Vm();
        vm.SetFilter("zzz");

        vm.SetFilter("");

        Assert.Equal(4, vm.VisibleOptions.Count);
        Assert.Equal(AppCommand.MoveDown, vm.Selected?.Value);
    }

    [Fact]
    public void FormatRows_Aligns_The_Label_Column_At_The_Help_Overlay_Width()
    {
        var vm = Vm();

        var rows = vm.FormatRows(width: 40);

        // Two leading spaces, hint padded to at least 8 (the string cheatsheet's column), one
        // separator space, then the label.
        Assert.Equal("  j        move down", rows[0]);
        Assert.Equal("  Enter    open selection", rows[2]);
    }

    [Fact]
    public void FormatRows_Widens_The_Hint_Column_For_A_Longer_Hint()
    {
        var vm = new MenuViewModel<AppCommand>(
        [
            new MenuOption<AppCommand>("previous unviewed file", "[v", AppCommand.PrevUnviewedFile),
            new MenuOption<AppCommand>("previous tab", "S-Tab", AppCommand.PrevTab),
            new MenuOption<AppCommand>("focus pane left", "Backspace", AppCommand.FocusLeft),
        ]);

        var rows = vm.FormatRows(width: 40);

        Assert.Equal("  [v        previous unviewed file", rows[0]);
        Assert.Equal("  Backspace focus pane left", rows[2]);
    }

    [Fact]
    public void SelectedIndex_Clamps_To_The_Filtered_Bound_Not_The_Full_Row_Count()
    {
        var vm = Vm();
        vm.SetFilter("en"); // move down, open selection

        vm.SelectedIndex = 99;

        Assert.Equal(1, vm.SelectedIndex);
        Assert.Equal(AppCommand.Open, vm.Selected?.Value);
    }

    [Fact]
    public void A_Filter_That_Matches_Nothing_Leaves_No_Row_To_Execute()
    {
        var vm = Vm();

        vm.SetFilter("zzz");

        Assert.Empty(vm.VisibleOptions);
        Assert.Empty(vm.FormatRows(width: 40));
        Assert.Null(vm.Selected);
        // Setting an index against an empty row set must not throw — the menu's render mirrors
        // the list's placeholder row back into it on every keystroke.
        vm.SelectedIndex = 0;
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void FormatRows_Keeps_The_Hint_Column_Steady_While_Filtering()
    {
        // The type's whole reason for an order-preserving filter is that hints must not jump
        // under the user's eyes; a column that re-flows when the widest hint is filtered out
        // shifts every surviving label just as badly.
        var vm = new MenuViewModel<AppCommand>(
        [
            new MenuOption<AppCommand>("previous unviewed file", "[v", AppCommand.PrevUnviewedFile),
            new MenuOption<AppCommand>("previous tab", "S-Tab", AppCommand.PrevTab),
            new MenuOption<AppCommand>("focus pane left", "Backspace", AppCommand.FocusLeft),
        ]);

        // "previous" drops the widest-hint row (Backspace) and keeps the other two.
        vm.SetFilter("previous");

        Assert.Equal(
            ["  [v        previous unviewed file", "  S-Tab     previous tab"],
            vm.FormatRows(width: 40));
    }

    [Fact]
    public void FormatRows_Truncates_To_The_Available_Width()
    {
        var vm = Vm();

        var rows = vm.FormatRows(width: 14);

        Assert.Equal("  Enter    ope", rows[2]);
        Assert.All(rows, r => Assert.True(r.Length <= 14, r));
    }

    [Fact]
    public void FormatRows_Renders_Only_The_Visible_Rows()
    {
        var vm = Vm();
        vm.SetFilter("en");

        Assert.Equal(2, vm.FormatRows(width: 40).Count);
    }
}
