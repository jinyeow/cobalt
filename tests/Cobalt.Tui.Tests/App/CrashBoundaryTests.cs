using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The crash boundary and background-fault hook, tested as functions (not via the
/// global event plumbing): they must write the exception to the log, tell the user,
/// and return a non-zero code.
/// </summary>
public class CrashBoundaryTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void HandleCrash_Writes_Log_Prints_Path_And_Returns_NonZero()
    {
        var dir = Path.Join(Path.GetTempPath(), $"cobalt-crash-{Guid.NewGuid():N}");
        var path = Path.Join(dir, "crash.log");
        var stderr = new StringWriter();
        try
        {
            var code = CobaltTuiApp.HandleCrash(new InvalidOperationException("boom"), path, When, stderr);

            Assert.NotEqual(0, code);
            Assert.True(File.Exists(path));
            Assert.Contains("boom", File.ReadAllText(path));
            Assert.Contains("cobalt crashed", stderr.ToString());
            Assert.Contains(path, stderr.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HandleCrash_With_Unwritable_Path_Does_Not_Throw_And_Falls_Back_To_Stderr()
    {
        var stderr = new StringWriter();

        // An empty path makes File.AppendAllText throw ArgumentException (not IOException),
        // exactly the class of failure the old `catch (IOException)` let escape.
        var code = CobaltTuiApp.HandleCrash(new InvalidOperationException("boom"), "", When, stderr);

        Assert.NotEqual(0, code);
        Assert.Contains("cobalt crashed", stderr.ToString());
        Assert.Contains("boom", stderr.ToString()); // fell back to dumping the stack
    }

    [Fact]
    public void LogBackgroundFault_With_Unwritable_Path_Does_Not_Throw()
    {
        // Must not throw even though the write fails with a non-IOException.
        CobaltTuiApp.LogBackgroundFault(new InvalidOperationException("boom"), "", When);
    }

    [Fact]
    public void LogBackgroundFault_Routes_To_The_Same_Writer()
    {
        var dir = Path.Join(Path.GetTempPath(), $"cobalt-crash-{Guid.NewGuid():N}");
        var path = Path.Join(dir, "crash.log");
        try
        {
            CobaltTuiApp.LogBackgroundFault(new InvalidOperationException("background boom"), path, When);

            Assert.True(File.Exists(path));
            Assert.Contains("background boom", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
