using Cobalt.Tui.Editor;
using Terminal.Gui.App;

namespace Cobalt.Tui.App;

/// <summary>
/// Binds an <see cref="ITerminalSuspender"/> to the live Terminal.Gui application:
/// queue work on the UI thread (<c>app.Invoke</c>), release the tty
/// (<c>Driver.Suspend()</c>), and repaint after the child exits
/// (<c>LayoutAndDraw(true)</c>).
///
/// <para>These three lambdas are the only part of the suspend/resume feature that
/// cannot be unit-tested: Terminal.Gui v2 exposes <c>Suspend()</c> but no public
/// resume (<c>IDriver.Init</c> throws), so <c>LayoutAndDraw(true)</c> is the
/// empirically verified restore. Correctness here is covered by the PTY smoke, not
/// unit tests; all orchestration lives in <see cref="UiThreadSuspender"/>.</para>
/// </summary>
public static class TerminalGuiSuspender
{
    public static ITerminalSuspender For(IApplication app) => new UiThreadSuspender(
        app.Invoke,
        () => app.Driver?.Suspend(),
        () => app.LayoutAndDraw(true));
}
