using System.Collections.ObjectModel;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.Screens;

public class ListNavigationTests
{
    [Fact]
    public void Apply_Moves_Without_Extending_The_Marked_Selection()
    {
        // With marking enabled, MoveDown(extend: true) would extend the marked
        // selection (marking rows 0 and 1). ListNavigation must pass extend: false,
        // so no rows end up marked.
        var list = new ListView { ShowMarks = true, MarkMultiple = true };
        list.SetSource(new ObservableCollection<string>(Enumerable.Range(0, 5).Select(i => $"row {i}")));
        list.SelectedItem = 0;

        ListNavigation.Apply(list, AppCommand.MoveDown);

        Assert.Equal(1, list.SelectedItem);
        Assert.Empty(list.GetAllMarkedItems());
    }

    [Theory]
    [InlineData(AppCommand.MoveDown)]
    [InlineData(AppCommand.MoveUp)]
    [InlineData(AppCommand.MoveTop)]
    [InlineData(AppCommand.MoveBottom)]
    [InlineData(AppCommand.HalfPageDown)]
    [InlineData(AppCommand.HalfPageUp)]
    public void Movement_Commands_Route_To_List_Navigation(AppCommand command)
    {
        Assert.True(ListNavigation.Applies(command));
    }

    [Theory]
    [InlineData(AppCommand.Open)]
    [InlineData(AppCommand.CommandPalette)]
    [InlineData(AppCommand.Vote)]
    [InlineData(AppCommand.NextTab)]
    [InlineData(AppCommand.YankId)]
    public void Non_Movement_Commands_Do_Not(AppCommand command)
    {
        Assert.False(ListNavigation.Applies(command));
    }
}
