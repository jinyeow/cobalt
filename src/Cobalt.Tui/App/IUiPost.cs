namespace Cobalt.Tui.App;

/// <summary>
/// The one-member UI-thread marshalling seam: hands a callback to the Terminal.Gui main loop
/// so background/threadpool continuations can touch widgets safely (ADR 0013). This replaces
/// passing the whole <c>IApplication</c> around just for its <c>Invoke</c>.
/// <para>
/// <see cref="Post"/> <em>queues</em> the action onto the UI main loop; it never runs it inline,
/// even when the caller is already on the UI thread. Callers rely on that: coalescing gates and
/// burst-collapsing renders depend on FIFO queue ordering, so an implementation that ran the
/// action synchronously would break their semantics.
/// </para>
/// </summary>
public interface IUiPost
{
    /// <summary>Queues <paramref name="action"/> onto the UI main loop (never runs it inline).</summary>
    void Post(Action action);
}
