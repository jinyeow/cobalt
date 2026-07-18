using Cobalt.Tui.App;
using Cobalt.Tui.Input;
using Terminal.Gui.Views;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell's per-key decision (CobaltShell.DecideKey). The load-bearing case is
/// B1: an Esc that the router doesn't bind must be consumed by the shell so it never
/// reaches the application-level Esc→Quit binding.
/// </summary>
public class ShellKeyDecisionTests
{
    [Fact]
    public void Unbound_Esc_Is_Consumed_And_Dispatches_Nothing()
    {
        var decision = CobaltShell.DecideKey("Esc", KeyResult.None);

        Assert.True(decision.Handled);
        Assert.Null(decision.Command);
    }

    [Fact]
    public void Unbound_Other_Key_Falls_Through_Unhandled()
    {
        var decision = CobaltShell.DecideKey("z", KeyResult.None);

        Assert.False(decision.Handled);
        Assert.Null(decision.Command);
    }

    [Fact]
    public void Pending_Sequence_Is_Consumed_Without_A_Command()
    {
        var decision = CobaltShell.DecideKey("g", KeyResult.Pending);

        Assert.True(decision.Handled);
        Assert.Null(decision.Command);
    }

    [Fact]
    public void Matched_Key_Is_Consumed_And_Carries_Its_Command()
    {
        var decision = CobaltShell.DecideKey("j", KeyResult.Matched(AppCommand.MoveDown));

        Assert.True(decision.Handled);
        Assert.Equal(AppCommand.MoveDown, decision.Command);
    }

    // ---- SHELL-2: SetIfChanged chrome guard ----

    [Fact]
    public void SetIfChanged_Is_A_NoOp_When_The_Text_Is_Unchanged()
    {
        var label = new Label { Text = "same" };

        // Unchanged: must not touch the label (so RefreshChrome doesn't dirty every chrome label
        // on every routine status/log message).
        Assert.False(CobaltShell.SetIfChanged(label, "same"));
        Assert.Equal("same", label.Text);
    }

    [Fact]
    public void SetIfChanged_Updates_And_Reports_A_Change()
    {
        var label = new Label { Text = "old" };

        Assert.True(CobaltShell.SetIfChanged(label, "new"));
        Assert.Equal("new", label.Text);
    }
}
