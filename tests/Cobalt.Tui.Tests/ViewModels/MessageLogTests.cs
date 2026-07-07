using Cobalt.Tui.ViewModels;

namespace Cobalt.Tui.Tests.ViewModels;

public class MessageLogTests
{
    [Fact]
    public void Error_Becomes_Current_Status()
    {
        var log = new MessageLog();

        log.Error("boom");

        Assert.Equal("boom", log.Current?.Text);
        Assert.Equal(MessageLevel.Error, log.Current?.Level);
    }

    [Fact]
    public void Keeps_History_Newest_Last()
    {
        var log = new MessageLog();

        log.Info("one");
        log.Error("two");

        Assert.Equal(["one", "two"], log.History.Select(m => m.Text));
    }

    [Fact]
    public void History_Is_Bounded()
    {
        var log = new MessageLog(capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            log.Info($"m{i}");
        }

        Assert.Equal(3, log.History.Count);
        Assert.Equal("m9", log.History[^1].Text);
    }

    [Fact]
    public void Raises_Changed_Event()
    {
        var log = new MessageLog();
        var raised = 0;
        log.Changed += () => raised++;

        log.Info("x");

        Assert.Equal(1, raised);
    }
}
