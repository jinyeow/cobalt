using Cobalt.Tui.Editor;
using Terminal.Gui.App;

namespace Cobalt.Tui.App;

/// <summary>
/// Binds an <see cref="ITerminalSuspender"/> to the live Terminal.Gui application:
/// queue work on the UI thread (<c>app.Invoke</c>), release the tty
/// (<c>Driver.Suspend()</c>), and resume after the child exits — clearing every cell,
/// forcing a full redraw, then re-establishing focus (see <see cref="Resume"/>).
///
/// <para>These three lambdas are the only part of the suspend/resume feature that
/// cannot be unit-tested: Terminal.Gui v2 exposes <c>Suspend()</c> but no public
/// resume (<c>IDriver.Init</c> throws), so the clear + <c>LayoutAndDraw(true)</c> +
/// re-focus sequence is the empirically verified restore. Correctness here is covered
/// by the PTY smoke, not unit tests; all orchestration lives in
/// <see cref="UiThreadSuspender"/>.</para>
/// </summary>
public static class TerminalGuiSuspender
{
    public static ITerminalSuspender For(IApplication app) => new UiThreadSuspender(
        app.Invoke,
        () => app.Driver?.Suspend(),
        Resume(app));

    // The child editor released and repainted the tty, so on the way back we must
    // (1) wipe every cell before redrawing, or stray escape codes linger in the
    // corner (a plain repaint only rewrites the cells TG thinks changed), and
    // (2) re-establish keyboard focus — Suspend/resume drops it, which otherwise
    // leaves the active dialog receiving no keys after `:wq`. Terminal-only
    // behavior; verified by UAT, not unit tests.
    private static Action Resume(IApplication app) => () =>
    {
        app.Driver?.ClearContents();
        app.LayoutAndDraw(true);
        app.TopRunnableView?.SetFocus();
    };
}
