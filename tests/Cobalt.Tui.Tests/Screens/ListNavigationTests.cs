using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;

namespace Cobalt.Tui.Tests.Screens;

public class ListNavigationTests
{
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
