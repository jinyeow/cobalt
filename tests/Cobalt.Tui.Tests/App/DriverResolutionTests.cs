using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Terminal.Gui.Drivers;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// Driver selection (ADR 0016): an explicit COBALT_DRIVER wins; a multiplexer/RDP session
/// selects 'dotnet'; otherwise the platform default is pinned explicitly ('windows' on
/// Windows, 'dotnet' elsewhere) — never Terminal.Gui's auto-detect, whose 2.4.17 pick (the
/// new 'ansi' driver) drops every other keypress.
/// </summary>
public class DriverResolutionTests
{
    private static readonly string[] Known = ["windows", "dotnet", "ansi"];

    private static Func<string, string?> Env(string? value) =>
        EnvVars(cobaltDriver: value);

    private static Func<string, string?> EnvVars(
        string? cobaltDriver = null, string? zellij = null, string? tmux = null,
        string? sessionName = null) =>
        name => name switch
        {
            "COBALT_DRIVER" => cobaltDriver,
            "ZELLIJ" => zellij,
            "TMUX" => tmux,
            "SESSIONNAME" => sessionName,
            _ => null,
        };

    [Fact]
    public void Unset_Pins_The_Windows_Driver_On_Windows()
    {
        // Never null: null would hand selection to TG auto-detect, which picks 'ansi' in
        // 2.4.17 — and the ansi driver eats every other keypress (the j/k double-press bug).
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(Env(null), Known, isWindows: true));
    }

    [Fact]
    public void Unset_Pins_The_Dotnet_Driver_Off_Windows()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(Env(null), Known, isWindows: false));
    }

    [Fact]
    public void Blank_Pins_The_Platform_Default()
    {
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(Env("   "), Known, isWindows: true));
    }

    [Fact]
    public void Valid_Value_Resolves_And_Trims()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(Env("  dotnet  "), Known, isWindows: true));
    }

    [Fact]
    public void Value_Is_Case_Insensitive_And_Canonicalized()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(Env("DotNet"), Known, isWindows: true));
    }

    [Fact]
    public void Explicit_Ansi_Is_Still_Honoured()
    {
        // The pin only governs the default; an explicit COBALT_DRIVER=ansi is a deliberate
        // user choice (e.g. to retest the upstream bug) and passes through.
        Assert.Equal("ansi", CobaltTuiApp.ResolveDriver(Env("ansi"), Known, isWindows: true));
    }

    [Fact]
    public void Unknown_Value_Throws_With_Actionable_Message()
    {
        var ex = Assert.Throws<ConfigException>(() => CobaltTuiApp.ResolveDriver(Env("dotnett"), Known, isWindows: true));
        Assert.Contains("dotnett", ex.Message);
        Assert.Contains("windows", ex.Message);
        Assert.Contains("dotnet", ex.Message);
    }

    [Fact]
    public void Zellij_Selects_The_Dotnet_Driver()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(zellij: "0"), Known, isWindows: true));
    }

    [Fact]
    public void Tmux_Selects_The_Dotnet_Driver()
    {
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(tmux: "/tmp/tmux-1000/default,1234,0"), Known, isWindows: true));
    }

    [Fact]
    public void Multiplexer_Without_A_Dotnet_Driver_Falls_Back_To_The_Platform_Pin()
    {
        // Defensive: if a future Terminal.Gui drops/renames the 'dotnet' driver, degrade to
        // the deterministic platform pin — never to TG auto-detect.
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(zellij: "0"), ["windows", "ansi"], isWindows: true));
    }

    [Fact]
    public void Platform_Pin_Missing_From_The_Registry_Falls_Back_To_Null()
    {
        // Last resort only: with even the pinned driver unregistered, null lets TG pick
        // rather than throwing into the crash boundary.
        Assert.Null(CobaltTuiApp.ResolveDriver(EnvVars(), ["ansi"], isWindows: true));
    }

    [Fact]
    public void Explicit_Value_Overrides_Multiplexer_Detection()
    {
        // COBALT_DRIVER=windows forces the Win32 driver even inside a multiplexer.
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(cobaltDriver: "windows", zellij: "0"), Known, isWindows: true));
    }

    [Fact]
    public void No_Override_And_No_Multiplexer_Pins_The_Platform_Default()
    {
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(), Known, isWindows: true));
    }

    [Theory]
    [InlineData("RDP-Tcp#0")]
    [InlineData("RDP-Tcp#42")]
    [InlineData("rdp-tcp#0")] // case-insensitive: the prefix is the signal, not its casing
    public void Rdp_Session_Selects_The_Dotnet_Driver(string sessionName)
    {
        // A remote/RDP session (e.g. a Windows 365 Cloud PC) paints through ConPTY's
        // console-buffer translation on the Win32 'windows' driver — expensive over a
        // latency link on a GPU-less host. The stdio/ANSI 'dotnet' driver skips it.
        Assert.Equal("dotnet", CobaltTuiApp.ResolveDriver(EnvVars(sessionName: sessionName), Known, isWindows: true));
    }

    [Fact]
    public void Console_Session_Pins_The_Windows_Driver()
    {
        // A physical console (SESSIONNAME=Console) gets the same explicit pin as any
        // non-multiplexed Windows terminal.
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(sessionName: "Console"), Known, isWindows: true));
    }

    [Fact]
    public void Explicit_Value_Overrides_Rdp_Detection()
    {
        // COBALT_DRIVER=windows forces the Win32 driver even in a remote session.
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(cobaltDriver: "windows", sessionName: "RDP-Tcp#0"), Known, isWindows: true));
    }

    [Fact]
    public void Rdp_Session_Without_A_Dotnet_Driver_Falls_Back_To_The_Platform_Pin()
    {
        Assert.Equal("windows", CobaltTuiApp.ResolveDriver(EnvVars(sessionName: "RDP-Tcp#0"), ["windows", "ansi"], isWindows: true));
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
