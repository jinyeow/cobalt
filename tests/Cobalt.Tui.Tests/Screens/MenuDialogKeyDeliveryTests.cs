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
            out var list);
        harness = new Harness { Vm = vm, Dialog = dialog, List = list };
        dialog.Layout(new Size(60, 12));
        dialog.SetFocus();
        return harness;
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
    public void Type_Ahead_Does_Not_Steal_The_Menus_Letters()
    {
        // A ListView's CollectionNavigator would otherwise consume 'r'/'q' as a search prefix
        // before the router sees them (the PrListView lesson).
        var menu = NewMenu();

        Assert.Null(menu.List.KeystrokeNavigator);
    }
}
