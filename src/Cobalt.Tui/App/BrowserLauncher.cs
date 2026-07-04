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
            var (file, args) = Command(url);
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static (string File, string Args) Command(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", $"/c start \"\" \"{url}\"");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ("open", url);
        }
        return ("xdg-open", url);
    }
}
