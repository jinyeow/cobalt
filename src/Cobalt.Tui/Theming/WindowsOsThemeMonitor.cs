using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Cobalt.Tui.Theming;

/// <summary>
/// Watches the Windows personalization setting that drives light/dark mode:
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c> value
/// <c>AppsUseLightTheme</c> (DWORD: <c>0</c> → dark, <c>1</c> → light, missing → unknown).
///
/// <para>The value → <see cref="OsTheme"/> mapping is unit-tested: the raw DWORD read is injected
/// behind a <see cref="Func{T}"/> so a test can drive it without touching the real registry or
/// starting the watch thread. The registry open + <c>RegNotifyChangeKeyValue</c> glue in
/// <see cref="WatchLoop"/> is a thin, un-unit-tested OS seam (as with the repo's other native
/// glue).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsThemeMonitor : IOsThemeMonitor
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";
    private const int RegNotifyChangeLastSet = 0x00000004;

    private readonly Func<int?> _read;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private Thread? _watcher;
    private bool _disposed;

    /// <summary>Production constructor: reads the real registry value.</summary>
    public WindowsOsThemeMonitor()
        : this(ReadAppsUseLightTheme)
    {
    }

    /// <summary>Test seam: injects the raw <c>AppsUseLightTheme</c> read.</summary>
    internal WindowsOsThemeMonitor(Func<int?> read) => _read = read;

    public OsTheme Current => Map(_read());

    public event Action<OsTheme>? Changed;

    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "cobalt-os-theme-watch",
        };
        _watcher.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _watcher?.Join();
        _stop.Dispose();
    }

    private static OsTheme Map(int? appsUseLightTheme) => appsUseLightTheme switch
    {
        0 => OsTheme.Dark,
        1 => OsTheme.Light,
        _ => OsTheme.Unknown,
    };

    private static int? ReadAppsUseLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as int?;
    }

    // Un-unit-tested OS seam: open the key, block on the kernel change notification, re-read and
    // raise Changed. Loops until Dispose signals _stop.
    private void WatchLoop()
    {
        while (!_stop.IsSet)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null)
            {
                _stop.Wait();
                return;
            }

            using var changed = new ManualResetEvent(initialState: false);
            int rc = RegNotifyChangeKeyValue(
                key.Handle,
                watchSubtree: false,
                RegNotifyChangeLastSet,
                changed.SafeWaitHandle,
                asynchronous: true);
            if (rc != 0)
            {
                _stop.Wait();
                return;
            }

            int signalled = WaitHandle.WaitAny([_stop.WaitHandle, changed]);
            if (signalled == 0)
            {
                return;
            }

            Changed?.Invoke(Current);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
