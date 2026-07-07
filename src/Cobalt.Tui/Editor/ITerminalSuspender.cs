namespace Cobalt.Tui.Editor;

/// <summary>
/// Runs a terminal-owning action with the TUI quiesced, then restores it. The
/// contract is strictly ordered: suspend → body → resume, with resume always
/// firing (even when suspend or the body throws). See <c>UiThreadSuspender</c>
/// for the production implementation and its threading rationale.
/// </summary>
public interface ITerminalSuspender
{
    /// <summary>Runs <paramref name="body"/> with the TUI suspended; suspend → body →
    /// resume, resume always fires (even when suspend or body throws).</summary>
    Task<int> RunSuspendedAsync(Func<int> body, CancellationToken cancellationToken = default);
}

/// <summary>No terminal to suspend — runs the body inline. Used by tests and any
/// headless construction of <see cref="ProcessEditorLauncher"/>.</summary>
public sealed class InlineTerminalSuspender : ITerminalSuspender
{
    public Task<int> RunSuspendedAsync(Func<int> body, CancellationToken cancellationToken = default)
    {
        // No terminal to suspend and no cleanup to guarantee, so there is nothing to
        // marshal: a cancelled token or a throwing body simply surfaces synchronously.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(body());
    }
}
