using Cobalt.Tui.Theming;

namespace Cobalt.Tui.Tests.Theming;

public class NoOpOsThemeMonitorTests
{
    [Fact]
    public void Current_is_Unknown()
    {
        using var monitor = new NoOpOsThemeMonitor();

        Assert.Equal(OsTheme.Unknown, monitor.Current);
    }

    [Fact]
    public void Start_and_Dispose_do_not_throw()
    {
        var monitor = new NoOpOsThemeMonitor();

        monitor.Start();
        monitor.Dispose();
    }
}

public class WindowsOsThemeMonitorTests
{
    [Theory]
    [InlineData(0, OsTheme.Dark)]
    [InlineData(1, OsTheme.Light)]
    [InlineData(null, OsTheme.Unknown)]
    public void Current_maps_AppsUseLightTheme(int? raw, OsTheme expected)
    {
        // WindowsOsThemeMonitor is [SupportedOSPlatform("windows")]; the early-return keeps CA1416
        // satisfied and skips the assertion on non-Windows CI. The injected reader means no real
        // registry access and no watch thread.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var monitor = new WindowsOsThemeMonitor(() => raw);

        Assert.Equal(expected, monitor.Current);
    }
}

public class OsThemeMonitorFactoryTests
{
    [Fact]
    public void Create_returns_a_monitor_that_disposes_safely()
    {
        IOsThemeMonitor monitor = OsThemeMonitor.Create();

        Assert.NotNull(monitor);
        monitor.Dispose();
    }
}
