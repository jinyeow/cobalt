using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Terminal.Gui.Drivers;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The COBALT_DRIVER escape hatch: forces a specific Terminal.Gui driver (needed under
/// terminal multiplexers like zellij/tmux, where TG's default Win32-console 'windows'
/// driver drops keystrokes and mishandles the editor suspend/resume). Unset = TG default.
/// </summary>
public class DriverResolutionTests
{
    private static readonly string[] Known = ["windows", "dotnet", "ansi"];

    private static Func<string, string?> Env(string? value) =>
        EnvVars(cobaltDriver: value);

    private static Func<string, string?> EnvVars(
        string? cobaltDriver = null, string? zellij = null, string? tmux = null, string? wtSession = null) =>
        name => name switch
        {
            "COBALT_DRIVER" => cobaltDriver,
            "ZELLIJ" => zellij,
            "TMUX" => tmux,
            "WT_SESSION" => wtSession,
            _ => null,
        };

    [Fact]
    public void Unset_Resolves_To_Null_Default()
    {
        Assert.Null(CobaltTuiApp.ResolveDriver(Env(null), Known));
    }

    [Fact]
    public void Blank_Resolves_To_Null_Default()
    {
        Assert.Null(CobaltTuiApp.ResolveDriver(Env("   "), Known));
    }

    [Fact]
    public void Valid_Value_Resolves_And_Trims()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(Env("  dotnet  "), Known));
    }

    [Fact]
    public void Value_Is_Case_Insensitive_And_Canonicalized()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(Env("DotNet"), Known));
    }

    [Fact]
    public void Unknown_Value_Throws_With_Actionable_Message()
    {
        var ex = Assert.Throws<ConfigException>(() => CobaltTuiApp.ResolveDriver(Env("dotnett"), Known));
        Assert.Contains("dotnett", ex.Message);
        Assert.Contains("windows", ex.Message);
        Assert.Contains("dotnet", ex.Message);
    }

    [Fact]
    public void Zellij_Selects_The_Dotnet_Driver()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(zellij: "0"), Known));
    }

    [Fact]
    public void Tmux_Selects_The_Dotnet_Driver()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(tmux: "/tmp/tmux-1000/default,1234,0"), Known));
    }

    [Fact]
    public void Windows_Terminal_Selects_The_Dotnet_Driver()
    {
        // WT_SESSION is set by Windows Terminal; the Win32-console driver mishandles the editor
        // handoff + console restore there, so auto-select the ANSI 'dotnet' driver.
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(wtSession: "abc-123"), Known));
    }

    [Fact]
    public void Explicit_Value_Overrides_Windows_Terminal_Detection()
    {
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(cobaltDriver: "windows", wtSession: "abc-123"), Known));
    }

    [Fact]
    public void Multiplexer_Without_A_Dotnet_Driver_Falls_Back_To_Null()
    {
        // Defensive: if a future Terminal.Gui drops/renames the 'dotnet' driver, auto-detect
        // degrades to TG's default rather than throwing (which would hit the crash boundary).
        Assert.Null(CobaltTuiApp.ResolveDriver(EnvVars(zellij: "0"), ["windows", "ansi"]));
    }

    [Fact]
    public void Explicit_Value_Overrides_Multiplexer_Detection()
    {
        // COBALT_DRIVER=windows forces the Win32 driver even inside a multiplexer.
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(cobaltDriver: "windows", zellij: "0"), Known));
    }

    [Fact]
    public void No_Override_And_No_Multiplexer_Is_Null()
    {
        Assert.Null(CobaltTuiApp.ResolveDriver(EnvVars(), Known));
    }

    [Fact]
    public void Registry_Exposes_The_Expected_Drivers_Before_Init()
    {
        // Guards that DriverRegistry.GetDriverNames() is callable statically (no Init) and
        // still names the drivers the escape hatch accepts.
        var names = DriverRegistry.GetDriverNames().ToArray();
        Assert.Contains("windows", names);
        Assert.Contains("dotnet", names);
    }
}
