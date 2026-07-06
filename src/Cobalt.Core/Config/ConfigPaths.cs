using System.Runtime.InteropServices;

namespace Cobalt.Core.Config;

public static class ConfigPaths
{
    public static string ConfigFile() => ConfigFile(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    public static string ConfigFile(Func<string, string?> env, string homeDirectory, bool isWindows) =>
        Path.Join(ConfigDirectory(env, homeDirectory, isWindows), "config.toml");

    public static string ConfigDirectory(Func<string, string?> env, string homeDirectory, bool isWindows)
    {
        var baseDir = isWindows
            ? env("APPDATA") ?? Path.Join(homeDirectory, "AppData", "Roaming")
            : env("XDG_CONFIG_HOME") ?? Path.Join(homeDirectory, ".config");
        return Path.Join(baseDir, "cobalt");
    }

    public static string ConfigDirectory() => ConfigDirectory(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>
    /// Where transient app state (logs) lives: XDG <c>state</c> on Unix
    /// (<c>$XDG_STATE_HOME</c> or <c>~/.local/state</c>), <c>%LOCALAPPDATA%</c> on
    /// Windows. Kept separate from the config dir so a crash log never sits next to
    /// user-edited configuration.
    /// </summary>
    public static string StateDirectory(Func<string, string?> env, string homeDirectory, bool isWindows)
    {
        var baseDir = isWindows
            ? env("LOCALAPPDATA") ?? Path.Join(homeDirectory, "AppData", "Local")
            : env("XDG_STATE_HOME") ?? Path.Join(homeDirectory, ".local", "state");
        return Path.Join(baseDir, "cobalt");
    }

    public static string StateDirectory() => StateDirectory(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>The crash-log file under the state directory.</summary>
    public static string CrashLogFile(Func<string, string?> env, string homeDirectory, bool isWindows) =>
        Path.Join(StateDirectory(env, homeDirectory, isWindows), "crash.log");

    public static string CrashLogFile() => Path.Join(StateDirectory(), "crash.log");
}
