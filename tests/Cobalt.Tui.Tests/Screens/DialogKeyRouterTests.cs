using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Terminal.Gui.Input;

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// Unit-level: drives the shared modal-dialog key adapter directly, with no view tree.
/// Pins the state machine the four dialogs used to hand-roll — Pending swallows, Matched
/// is handled only when the dialog acted, and Esc clears a pending sequence before it closes.
/// </summary>
public class DialogKeyRouterTests
{
    private sealed record Dispatched(AppCommand Command, int? Count);

    private static DialogKeyRouter Build(
        List<Dispatched> dispatched, bool acts = true, Action? requestClose = null) =>
        new(
            KeyBindingTable.Shared,
            KeyScope.WorkItemDetail,
            (command, count) =>
            {
                dispatched.Add(new Dispatched(command, count));
                return acts;
            },
            requestClose ?? (() => { }));

    [Fact]
    public void A_Pending_Sequence_Is_Swallowed_Without_Dispatching()
    {
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched);
        var key = new Key('g'); // prefix of "g g" / "g x" — nothing has matched yet

        router.HandleKey(null, key);

        Assert.True(key.Handled);
        Assert.Empty(dispatched);
    }

    [Fact]
    public void A_Match_The_Dialog_Acts_On_Is_Handled()
    {
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched);
        var key = new Key('j');

        router.HandleKey(null, key);

        Assert.True(key.Handled);
        Assert.Equal(new Dispatched(AppCommand.MoveDown, null), Assert.Single(dispatched));
    }

    [Fact]
    public void A_Match_The_Dialog_Ignores_Falls_Through_To_Native_Behavior()
    {
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched, acts: false);
        var key = new Key('j');

        router.HandleKey(null, key);

        Assert.False(key.Handled); // the widget's own scrolling still runs
        Assert.Single(dispatched);
    }

    [Fact]
    public void A_Count_Prefix_Reaches_The_Dispatch()
    {
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched);

        router.HandleKey(null, new Key('5'));
        router.HandleKey(null, new Key('j'));

        Assert.Equal(new Dispatched(AppCommand.MoveDown, 5), Assert.Single(dispatched));
    }

    [Fact]
    public void Esc_Clears_A_Pending_Sequence_Instead_Of_Closing()
    {
        var closed = 0;
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched, requestClose: () => closed++);
        router.HandleKey(null, new Key('g'));

        var esc = Key.Esc;
        router.HandleKey(null, esc);

        Assert.True(esc.Handled);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void Esc_With_Nothing_Pending_Closes()
    {
        var closed = 0;
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched, requestClose: () => closed++);

        var esc = Key.Esc;
        router.HandleKey(null, esc);

        Assert.True(esc.Handled);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void An_Untokenizable_Key_Is_Left_Alone()
    {
        var dispatched = new List<Dispatched>();
        var router = Build(dispatched);
        var key = Key.PageDown; // no token, so no binding can ever see it

        router.HandleKey(null, key);

        Assert.False(key.Handled);
        Assert.Empty(dispatched);
    }
}
