namespace Cobalt.Tui.Theming;

/// <summary>Creates the <see cref="IOsThemeMonitor"/> appropriate for the current OS.</summary>
public static class OsThemeMonitor
{
    /// <summary>
    /// A Windows registry watcher on Windows; the safe <see cref="NoOpOsThemeMonitor"/> elsewhere.
    /// </summary>
    public static IOsThemeMonitor Create() =>
        OperatingSystem.IsWindows() ? new WindowsOsThemeMonitor() : new NoOpOsThemeMonitor();
}
