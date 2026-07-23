using Terminal.Gui.App;

namespace Cobalt.Tui.App;

/// <summary>
/// Production <see cref="IUiPost"/>: a one-line pass-through to <see cref="IApplication.Invoke(Action)"/>,
/// which queues the action onto Terminal.Gui's main loop in FIFO order. Kept trivial on purpose —
/// it must never short-circuit to run inline when already on the UI thread, because the coalescing
/// gates that post through it depend on that queue ordering (see <see cref="IUiPost"/>).
/// </summary>
public sealed class ApplicationUiPost(IApplication app) : IUiPost
{
    public void Post(Action action) => app.Invoke(action);
}
