using Cobalt.Core.Config;

namespace Cobalt.Core.Tests.Config;

public class ConfigPathsTests
{
    [Fact]
    public void Uses_XdgConfigHome_When_Set()
    {
        var path = ConfigPaths.ConfigFile(
            env: name => name == "XDG_CONFIG_HOME" ? "/custom/xdg" : null,
            homeDirectory: "/home/u",
            isWindows: false);

        Assert.Equal("/custom/xdg/cobalt/config.toml", path);
    }

    [Fact]
    public void Falls_Back_To_DotConfig_Under_Home()
    {
        var path = ConfigPaths.ConfigFile(env: _ => null, homeDirectory: "/home/u", isWindows: false);

        Assert.Equal("/home/u/.config/cobalt/config.toml", path);
    }

    [Fact]
    public void Uses_AppData_On_Windows()
    {
        var path = ConfigPaths.ConfigFile(
            env: name => name == "APPDATA" ? @"C:\Users\u\AppData\Roaming" : null,
            homeDirectory: @"C:\Users\u",
            isWindows: true);

        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "cobalt", "config.toml"), path);
    }
}
