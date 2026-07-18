using System.Collections.ObjectModel;
using System.Drawing;
using Cobalt.Tui.Input;
using Cobalt.Tui.Screens;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

#pragma warning disable CS0618 // asserting on the read-only TextView pane the dialogs build

namespace Cobalt.Tui.Tests.Screens;

/// <summary>
/// The shared, count-aware scroll seam. Tests assert on <c>ListView.SelectedItem</c>
/// and <c>TextView.CurrentRow</c> — never <c>Viewport.Y</c>, which clamps at
/// Height-1 on TextView and so is not a reliable scroll offset (probed on TG 2.4.16).
/// </summary>
public class VimScrollTests
{
    private static ListView LaidOutList(int rows = 50, int width = 40, int height = 12)
    {
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        list.SetSource(new ObservableCollection<string>(Enumerable.Range(0, rows).Select(i => $"row {i}")));
        var window = new Window();
        window.Add(list);
        window.Layout(new Size(width, height));
        list.SelectedItem = 0;
        return list;
    }

    private static TextView LaidOutTextView(int lines = 200, int width = 40, int height = 12)
    {
        var tv = new TextView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i}")),
        };
        var window = new Window();
        window.Add(tv);
        window.Layout(new Size(width, height));
        return tv;
    }

    private static TextView LaidOutWrappedTextView(int lines = 30, int width = 30, int height = 12)
    {
        // Long lines (well past the ~inner-width viewport) so WordWrap breaks each logical
        // line into several visual rows: Lines (wrapped) must exceed the logical line count.
        var body = string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i} " + new string('x', 200)));
        var tv = new TextView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            Text = body,
        };
        var window = new Window();
        window.Add(tv);
        window.Layout(new Size(width, height));
        return tv;
    }

    [Theory]
    [InlineData(AppCommand.MoveDown)]
    [InlineData(AppCommand.MoveUp)]
    [InlineData(AppCommand.MoveTop)]
    [InlineData(AppCommand.MoveBottom)]
    [InlineData(AppCommand.HalfPageDown)]
    [InlineData(AppCommand.HalfPageUp)]
    public void Movement_Commands_Apply(AppCommand command)
    {
        Assert.True(VimScroll.Applies(command));
    }

    [Theory]
    [InlineData(AppCommand.Open)]
    [InlineData(AppCommand.CommandPalette)]
    [InlineData(AppCommand.Vote)]
    [InlineData(AppCommand.NextTab)]
    [InlineData(AppCommand.YankId)]
    public void Non_Movement_Commands_Do_Not_Apply(AppCommand command)
    {
        Assert.False(VimScroll.Applies(command));
    }

    // ---- ListView (SelectedItem) ----

    [Fact]
    public void MoveDown_No_Count_Advances_One()
    {
        var list = LaidOutList();

        VimScroll.Apply(list, AppCommand.MoveDown, null);

        Assert.Equal(1, list.SelectedItem);
    }

    [Fact]
    public void MoveDown_With_Count_Advances_By_Count()
    {
        var list = LaidOutList();

        VimScroll.Apply(list, AppCommand.MoveDown, 5);

        Assert.Equal(5, list.SelectedItem);
    }

    [Fact]
    public void MoveUp_With_Count()
    {
        var list = LaidOutList();
        list.SelectedItem = 10;

        VimScroll.Apply(list, AppCommand.MoveUp, 4);

        Assert.Equal(6, list.SelectedItem);
    }

    [Fact]
    public void MoveDown_Does_Not_Mark_The_Row()
    {
        // Plain cursor movement, not a marked-selection extend. With marking enabled,
        // an extending move would mark rows; Command.Down must leave none marked.
        var list = new ListView { ShowMarks = true, MarkMultiple = true };
        list.SetSource(new ObservableCollection<string>(Enumerable.Range(0, 5).Select(i => $"row {i}")));
        list.SelectedItem = 0;

        VimScroll.Apply(list, AppCommand.MoveDown, null);

        Assert.Equal(1, list.SelectedItem);
        Assert.Empty(list.GetAllMarkedItems());
    }

    [Fact]
    public void MoveBottom_No_Count_Goes_Last()
    {
        var list = LaidOutList(rows: 50);

        VimScroll.Apply(list, AppCommand.MoveBottom, null);

        Assert.Equal(49, list.SelectedItem);
    }

    [Fact]
    public void MoveDown_From_A_Null_Selection_Selects_The_First_Row()
    {
        // Mirror TG's own MoveDown: from no selection j lands on the first row, not the second.
        var list = LaidOutList(rows: 50);
        list.SelectedItem = null;

        VimScroll.Apply(list, AppCommand.MoveDown, null);

        Assert.Equal(0, list.SelectedItem);
    }

    [Fact]
    public void MoveUp_From_A_Null_Selection_Selects_The_Last_Row()
    {
        // Mirror TG's own MoveUp: from no selection k wraps to the last row, not clamps at the top.
        var list = LaidOutList(rows: 50);
        list.SelectedItem = null;

        VimScroll.Apply(list, AppCommand.MoveUp, null);

        Assert.Equal(49, list.SelectedItem);
    }

    [Fact]
    public void MoveBottom_Scrolls_The_Selected_Row_Into_View()
    {
        // The ListView path sets SelectedItem directly, so it must also scroll it on screen —
        // the last row of a 50-row list cannot sit inside a 12-row viewport at offset 0.
        var list = LaidOutList(rows: 50, height: 12);

        VimScroll.Apply(list, AppCommand.MoveBottom, null);

        var top = list.Viewport.Y;
        var selected = list.SelectedItem!.Value;
        Assert.InRange(selected, top, top + list.Viewport.Height - 1); // within the visible window
        Assert.True(top > 0, "the viewport scrolled down to reveal the last row");
    }

    [Fact]
    public void MoveBottom_With_Count_Goes_To_Line_N()
    {
        // "3G" jumps to line 3 (1-based) → index 2.
        var list = LaidOutList(rows: 50);
        list.SelectedItem = 20;

        VimScroll.Apply(list, AppCommand.MoveBottom, 3);

        Assert.Equal(2, list.SelectedItem);
    }

    [Fact]
    public void MoveTop_No_Count_Goes_First()
    {
        var list = LaidOutList(rows: 50);
        list.SelectedItem = 30;

        VimScroll.Apply(list, AppCommand.MoveTop, null);

        Assert.Equal(0, list.SelectedItem);
    }

    [Fact]
    public void MoveTop_With_Count_Goes_To_Line_N()
    {
        var list = LaidOutList(rows: 50);
        list.SelectedItem = 30;

        VimScroll.Apply(list, AppCommand.MoveTop, 3);

        Assert.Equal(2, list.SelectedItem);
    }

    [Fact]
    public void HalfPageDown_Moves_A_Half_Page()
    {
        var list = LaidOutList(rows: 50);
        var half = Math.Max(1, list.Viewport.Height / 2);

        VimScroll.Apply(list, AppCommand.HalfPageDown, null);

        Assert.Equal(half, list.SelectedItem);
    }

    [Fact]
    public void HalfPageDown_With_Count_Multiplies()
    {
        var list = LaidOutList(rows: 100);
        var half = Math.Max(1, list.Viewport.Height / 2);

        VimScroll.Apply(list, AppCommand.HalfPageDown, 2);

        Assert.Equal(half * 2, list.SelectedItem);
    }

    // ---- TextView (CurrentRow) ----

    [Fact]
    public void TextView_MoveDown_Advances_CurrentRow()
    {
        var tv = LaidOutTextView();

        VimScroll.Apply(tv, AppCommand.MoveDown, null);

        Assert.Equal(1, tv.CurrentRow);
    }

    [Fact]
    public void TextView_MoveDown_With_Count()
    {
        var tv = LaidOutTextView();

        VimScroll.Apply(tv, AppCommand.MoveDown, 5);

        Assert.Equal(5, tv.CurrentRow);
    }

    [Fact]
    public void TextView_MoveBottom_Scrolls_The_Last_Page_Into_View()
    {
        // Pager semantics: G shows the last page (last line at the bottom), so the top
        // visible row — which the invisible caret is pinned to — is lines - height.
        var tv = LaidOutTextView(lines: 200);
        var maxTop = tv.Lines - tv.Viewport.Height;

        VimScroll.Apply(tv, AppCommand.MoveBottom, null);

        Assert.Equal(maxTop, tv.CurrentRow);
    }

    [Fact]
    public void TextView_Cannot_Scroll_Past_The_Last_Page()
    {
        var tv = LaidOutTextView(lines: 200);
        var maxTop = tv.Lines - tv.Viewport.Height;

        VimScroll.Apply(tv, AppCommand.MoveBottom, null);
        VimScroll.Apply(tv, AppCommand.MoveDown, 10); // try to run off the end

        Assert.Equal(maxTop, tv.CurrentRow); // clamped at the last page
    }

    [Fact]
    public void TextView_MoveBottom_Reveals_Last_Content_When_WordWrapped()
    {
        var tv = LaidOutWrappedTextView(lines: 30);

        // Gate: prove wrapping actually happened headlessly, else the test proves nothing.
        Assert.True(tv.Lines > 30, $"expected wrapped rows > 30 logical lines, got {tv.Lines}");
        var maxTop = tv.Lines - tv.Viewport.Height;

        VimScroll.Apply(tv, AppCommand.MoveBottom, null);
        var afterG = tv.CurrentRow;
        VimScroll.Apply(tv, AppCommand.MoveDown, null); // a further move must not scroll past

        Assert.Equal(afterG, tv.CurrentRow);           // already at the bottom
        Assert.Equal(maxTop, tv.CurrentRow);           // last wrapped row sits at viewport bottom
    }

    [Fact]
    public void TextView_MoveTop_Goes_To_First_Row()
    {
        var tv = LaidOutTextView(lines: 200);
        VimScroll.Apply(tv, AppCommand.MoveBottom, null);

        VimScroll.Apply(tv, AppCommand.MoveTop, null);

        Assert.Equal(0, tv.CurrentRow);
    }

    [Fact]
    public void TextView_HalfPageDown_Advances_CurrentRow()
    {
        var tv = LaidOutTextView(lines: 200);
        var half = Math.Max(1, tv.Viewport.Height / 2);

        VimScroll.Apply(tv, AppCommand.HalfPageDown, null);

        Assert.Equal(half, tv.CurrentRow);
    }
}
