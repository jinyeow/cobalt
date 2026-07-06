using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class ConfigPathsTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    [Fact]
    public void Uses_XdgConfigHome_When_Set()
    {
        var path = ConfigPaths.ConfigFile(
            env: name => name == "XDG_CONFIG_HOME" ? "/custom/xdg" : null,
            homeDirectory: "/home/u",
            isWindows: false);

        // ConfigPaths uses Path.Join, so the separator is the host OS's ('\' on
        // Windows CI). Build the expected value explicitly (not by calling
        // Path.Join again) so the exact composed path is pinned.
        Assert.Equal($"/custom/xdg{Sep}cobalt{Sep}config.toml", path);
    }

    [Fact]
    public void Falls_Back_To_DotConfig_Under_Home()
    {
        var path = ConfigPaths.ConfigFile(env: _ => null, homeDirectory: "/home/u", isWindows: false);

        Assert.Equal($"/home/u{Sep}.config{Sep}cobalt{Sep}config.toml", path);
    }

    [Fact]
    public void Uses_AppData_On_Windows()
    {
        var path = ConfigPaths.ConfigFile(
            env: name => name == "APPDATA" ? @"C:\Users\u\AppData\Roaming" : null,
            homeDirectory: @"C:\Users\u",
            isWindows: true);

        Assert.Equal($@"C:\Users\u\AppData\Roaming{Sep}cobalt{Sep}config.toml", path);
    }
}
