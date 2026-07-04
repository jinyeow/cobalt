using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cobalt.Tui.App;

/// <summary>Opens a URL in the OS default browser (best-effort; failures are reported, not thrown).</summary>
public static class BrowserLauncher
{
    public static bool TryOpen(string url, out string? error)
    {
        try
        {
            using var process = Process.Start(StartInfo(url));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ProcessStartInfo StartInfo(string url)
    {
        // On Windows, the shell "open" verb (UseShellExecute) launches the default
        // browser without routing the URL through cmd.exe metacharacter parsing.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo(url) { UseShellExecute = true };
        }
        var opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
        return new ProcessStartInfo(opener, url) { UseShellExecute = false };
    }
}
