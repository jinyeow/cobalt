using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

public class CrashLogTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Format_Includes_Type_Message_And_Stack()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("kaboom");
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        var text = CrashLog.Format(captured, When);

        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("kaboom", text);
        Assert.Contains("2026-07-06T13:30:00", text);
        Assert.Contains("CrashLogTests", text); // a stack frame from this test
    }

    [Fact]
    public void Write_Creates_Directory_And_Returns_Path()
    {
        var dir = Path.Join(Path.GetTempPath(), $"cobalt-crash-{Guid.NewGuid():N}", "nested");
        var path = Path.Join(dir, "crash.log");
        try
        {
            var returned = CrashLog.Write(path, new InvalidOperationException("boom"), When);

            Assert.Equal(path, returned);
            Assert.True(File.Exists(path));
            Assert.Contains("boom", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(dir)!))
            {
                Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
            }
        }
    }

    [Fact]
    public void Write_Appends_Rather_Than_Overwrites()
    {
        var dir = Path.Join(Path.GetTempPath(), $"cobalt-crash-{Guid.NewGuid():N}");
        var path = Path.Join(dir, "crash.log");
        try
        {
            CrashLog.Write(path, new InvalidOperationException("first"), When);
            CrashLog.Write(path, new InvalidOperationException("second"), When);

            var text = File.ReadAllText(path);
            Assert.Contains("first", text);
            Assert.Contains("second", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
