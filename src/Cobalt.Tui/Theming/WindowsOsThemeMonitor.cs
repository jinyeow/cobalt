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
/// starting the watch thread. The watch loop's ordering (arm-before-read) and retry-on-failure
/// policy live in <see cref="ThemeWatchLoop"/> and are unit-tested through <see cref="IThemeWatchOps"/>;
/// only the thin <c>RegNotifyChangeKeyValue</c> glue in <see cref="RegistryWatchOps"/> is an
/// un-unit-tested OS seam (as with the repo's other native glue).</para>
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
        if (_disposed || _watcher is not null)
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

    private void WatchLoop()
    {
        using var ops = new RegistryWatchOps(_read, _stop);
        ThemeWatchLoop.Run(ops, () => _stop.IsSet, os => Changed?.Invoke(os));
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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    /// <summary>
    /// The native side of the watch: open the Personalize key, arm a one-shot
    /// <c>RegNotifyChangeKeyValue</c>, and block on it (or the stop signal). Backoff after a
    /// failure is capped exponential and resets once arming succeeds again. All of the ordering
    /// and retry decisions are made by <see cref="ThemeWatchLoop"/>; this type only performs them.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class RegistryWatchOps(Func<int?> read, ManualResetEventSlim stop) : IThemeWatchOps
    {
        private const int BackoffBaseMs = 1000;
        private const int BackoffCapMs = 30_000;

        private RegistryKey? _key;
        private ManualResetEvent? _changed;
        private int _backoffMs;

        public OsTheme ReadTheme() => Map(read());

        public bool TryArm()
        {
            // Release the previous iteration's handles before re-arming (the notification is
            // one-shot, so each arm needs a fresh key handle and event).
            _changed?.Dispose();
            _changed = null;
            _key?.Dispose();

            _key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (_key is null)
            {
                return false;
            }

            _changed = new ManualResetEvent(initialState: false);
            int rc = RegNotifyChangeKeyValue(
                _key.Handle,
                watchSubtree: false,
                RegNotifyChangeLastSet,
                _changed.SafeWaitHandle,
                asynchronous: true);
            if (rc != 0)
            {
                return false;
            }

            _backoffMs = 0;
            return true;
        }

        public bool WaitForChangeOrStop()
        {
            if (_changed is null)
            {
                stop.Wait();
                return true;
            }
            return WaitHandle.WaitAny([stop.WaitHandle, _changed]) == 0;
        }

        public bool BackoffOrStop()
        {
            _backoffMs = _backoffMs == 0 ? BackoffBaseMs : Math.Min(_backoffMs * 2, BackoffCapMs);
            return stop.Wait(_backoffMs);
        }

        public void Dispose()
        {
            // Close the key before the event: closing the key flushes any still-pending one-shot
            // notification (which fires by signalling the event) before the event handle is freed,
            // so the kernel never signals an already-released — and possibly reused — handle.
            _key?.Dispose();
            _changed?.Dispose();
        }
    }
}
