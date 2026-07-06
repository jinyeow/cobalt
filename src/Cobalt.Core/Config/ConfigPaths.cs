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
}
