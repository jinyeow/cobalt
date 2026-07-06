using System.Drawing;
using Cobalt.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.Input;

#pragma warning disable CS0618 // asserting on the read-only TextView pane TextDialog builds

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// View-level, headless: the `?`/`:messages`/key-reference overlay must vim-scroll —
/// a ReadOnly TextView otherwise swallows j/k/G. Asserts on CurrentRow, never Viewport.Y
/// (TG 2.4.16, ADR 0014).
/// </summary>
public class TextDialogScrollTests
{
    private static readonly IApplication App = Application.Create();

    private static Terminal.Gui.Views.TextView LaidOut(out Terminal.Gui.Views.Dialog dialog)
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        dialog = TextDialog.Build(App, "keys", text, out var view);
        dialog.Layout(new Size(60, 12));
        dialog.SetFocus();
        return view;
    }

    [Fact]
    public void J_Scrolls_The_Overlay_Down_One_Row()
    {
        var view = LaidOut(out var dialog);

        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(1, view.CurrentRow);
    }

    [Fact]
    public void G_Jumps_To_The_Last_Row_And_gg_Back_To_Top()
    {
        var view = LaidOut(out var dialog);

        dialog.NewKeyDownEvent(new Key('G'));
        Assert.Equal(199, view.CurrentRow);

        dialog.NewKeyDownEvent(new Key('g'));
        dialog.NewKeyDownEvent(new Key('g'));
        Assert.Equal(0, view.CurrentRow);
    }

    [Fact]
    public void Count_Then_J_Scrolls_By_Count()
    {
        var view = LaidOut(out var dialog);

        dialog.NewKeyDownEvent(new Key('5'));
        dialog.NewKeyDownEvent(new Key('j'));

        Assert.Equal(5, view.CurrentRow);
    }
}
