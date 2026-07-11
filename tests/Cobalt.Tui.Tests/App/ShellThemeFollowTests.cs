using Cobalt.Core.Config;
using Cobalt.Tui.App;
using Cobalt.Tui.Theming;
using Cobalt.Tui.ViewModels;
using Terminal.Gui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shell's OS-follow wiring, driven headlessly through a fake <see cref="IOsThemeMonitor"/>.
/// Touches Terminal.Gui's global theme statics, so it shares the non-parallel theming collection.
/// The rendered repaint is PTY/manual per ADR 0010; here we assert the ambient palette state.
/// </summary>
[Collection(nameof(Cobalt.Tui.Tests.Theming.ThemeServiceTests))]
public class ShellThemeFollowTests
{
    private static readonly IApplication App = Application.Create();

    private sealed class FakeOsThemeMonitor : IOsThemeMonitor
    {
        public OsTheme Current { get; set; } = OsTheme.Dark;
        public bool Started { get; private set; }
        public event Action<OsTheme>? Changed;
        public bool HasSubscriber => Changed is not null;
        public void Start() => Started = true;
        public void Raise(OsTheme os)
        {
            Current = os;
            Changed?.Invoke(os);
        }
        public void Dispose()
        {
        }
    }

    private static ShellViewModel Vm(ThemeChoice theme) =>
        new(["ctx"], "ctx", PrScope.Org, theme);

    [Fact]
    public void Construction_Starts_The_Monitor_And_Subscribes()
    {
        var monitor = new FakeOsThemeMonitor();

        using var shell = new CobaltShell(App, Vm(ThemeChoice.System), themeMonitor: monitor);

        Assert.True(monitor.Started);
        Assert.True(monitor.HasSubscriber);
    }

    [Fact]
    public void Os_Going_Light_While_Following_System_Applies_The_Light_Palette()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown)); // baseline: dark
        var monitor = new FakeOsThemeMonitor { Current = OsTheme.Dark };
        using var shell = new CobaltShell(App, Vm(ThemeChoice.System), themeMonitor: monitor);

        // The path OnOsThemeChanged marshals onto the UI thread; call it directly (a non-running
        // app never pumps its Invoke queue) to observe the follow behaviour.
        shell.ApplyOsFollow(OsTheme.Light);

        Assert.NotEqual(DiffPalette.Dark, ThemeService.CurrentPalette);
        Assert.Equal(ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown).Diff, ThemeService.CurrentPalette);
    }

    [Fact]
    public void Theme_System_Command_Through_The_Shell_Resolves_Against_The_Monitor()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown)); // baseline: dark
        var monitor = new FakeOsThemeMonitor { Current = OsTheme.Light };
        var vm = Vm(ThemeChoice.Dark);
        using var shell = new CobaltShell(App, vm, themeMonitor: monitor);

        // Drives `:theme system` end-to-end through the shell (OnThemeChangeRequested), which the
        // VM-only and OS-follow tests never exercise. `system` resolves against _themeMonitor.Current,
        // so a light monitor must yield the light palette — this pins the monitor read itself: if the
        // `?? OsTheme.Unknown` fallback regressed to ignore Current, resolution would fall back to dark.
        vm.HandlePaletteInput("theme system");

        Assert.Equal(ThemeResolver.Resolve(ThemeChoice.Light, OsTheme.Unknown).Diff, ThemeService.CurrentPalette);
    }

    [Fact]
    public void Os_Change_With_A_Fixed_Theme_Leaves_The_Palette_Untouched()
    {
        ThemeService.Enable();
        ThemeService.Apply(ThemeResolver.Resolve(ThemeChoice.Dark, OsTheme.Unknown)); // fixed dark
        var monitor = new FakeOsThemeMonitor { Current = OsTheme.Dark };
        using var shell = new CobaltShell(App, Vm(ThemeChoice.Dark), themeMonitor: monitor);

        shell.ApplyOsFollow(OsTheme.Light);

        Assert.Equal(DiffPalette.Dark, ThemeService.CurrentPalette);
    }

    [Fact]
    public void Disposing_The_Shell_Unsubscribes_From_The_Monitor()
    {
        var monitor = new FakeOsThemeMonitor();
        var shell = new CobaltShell(App, Vm(ThemeChoice.System), themeMonitor: monitor);
        Assert.True(monitor.HasSubscriber);

        shell.Dispose();

        Assert.False(monitor.HasSubscriber);
    }
}
