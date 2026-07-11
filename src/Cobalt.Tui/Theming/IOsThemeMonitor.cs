namespace Cobalt.Tui.Theming;

/// <summary>
/// Detects the OS light/dark setting and notifies when it changes, so <c>theme = "system"</c>
/// can follow it live. A Windows implementation watches the registry; other platforms get a
/// safe no-op that reports <see cref="OsTheme.Unknown"/> (best-effort, follow-up work).
/// </summary>
public interface IOsThemeMonitor : IDisposable
{
    /// <summary>The current OS theme, or <see cref="OsTheme.Unknown"/> if undetectable.</summary>
    OsTheme Current { get; }

    /// <summary>Raised when the OS theme changes (never raised by the no-op implementation).</summary>
    event Action<OsTheme>? Changed;

    /// <summary>Begins watching for changes; safe to call once after construction.</summary>
    void Start();
}
