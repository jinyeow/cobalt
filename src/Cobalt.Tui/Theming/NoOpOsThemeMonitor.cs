namespace Cobalt.Tui.Theming;

/// <summary>
/// The fallback <see cref="IOsThemeMonitor"/> for platforms where cobalt cannot detect the OS
/// theme (non-Windows, or Windows without the setting). Reports <see cref="OsTheme.Unknown"/>
/// and never raises <see cref="Changed"/>.
/// </summary>
public sealed class NoOpOsThemeMonitor : IOsThemeMonitor
{
    public OsTheme Current => OsTheme.Unknown;

    // Empty accessors (no backing field) satisfy the interface without a field-like event that
    // would trip CS0067 for never being raised.
    public event Action<OsTheme>? Changed { add { } remove { } }

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
