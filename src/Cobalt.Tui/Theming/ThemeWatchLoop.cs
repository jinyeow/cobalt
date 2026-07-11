namespace Cobalt.Tui.Theming;

/// <summary>
/// The native primitives the OS-theme watch loop drives, behind an interface so the loop's
/// control flow is deterministically unit-testable without touching the real registry.
/// </summary>
internal interface IThemeWatchOps
{
    /// <summary>The current OS theme — sampled for the baseline and again after each arm.</summary>
    OsTheme ReadTheme();

    /// <summary>
    /// Arms a one-shot change notification on the OS-theme source. Returns <c>false</c> when it
    /// can't (the source is missing, or the native call failed) so the loop backs off and retries
    /// rather than giving up.
    /// </summary>
    bool TryArm();

    /// <summary>Blocks until the armed notification fires or stop is signalled; <c>true</c> ⇒ stopped.</summary>
    bool WaitForChangeOrStop();

    /// <summary>
    /// Waits a bounded backoff after an arm failure. Returns <c>true</c> if stop was signalled
    /// during the wait (exit the loop), <c>false</c> to retry.
    /// </summary>
    bool BackoffOrStop();
}

/// <summary>
/// The OS-agnostic control flow of the OS-theme watcher. Two things matter here and neither is
/// unit-testable inside the native seam, which is why the loop is factored out:
///
/// <list type="number">
/// <item><description><b>Arm before read.</b> The change notification is armed <em>before</em> the
/// value is sampled, so a write that races the read still fires the already-armed notification and
/// is picked up on the next iteration — no lost change (the classic one-shot re-registration gap).</description></item>
/// <item><description><b>Retry, don't park.</b> When arming fails the loop backs off and retries
/// instead of parking forever, so a transient failure (or a momentarily-absent key) self-heals
/// once the source reappears.</description></item>
/// </list>
///
/// <para>It also de-dups: <c>onChanged</c> fires only when the mapped theme actually changed, so a
/// spurious wake (any write under the watched key) doesn't trigger a redundant repaint.</para>
/// </summary>
internal static class ThemeWatchLoop
{
    public static void Run(IThemeWatchOps ops, Func<bool> stopped, Action<OsTheme> onChanged)
    {
        // Baseline: the value already applied at startup, so we don't re-raise it.
        OsTheme last = ops.ReadTheme();

        while (!stopped())
        {
            if (!ops.TryArm())
            {
                if (ops.BackoffOrStop())
                {
                    return;
                }
                continue;
            }

            // Read AFTER arming: a write between here and the wait still fires the armed event.
            OsTheme now = ops.ReadTheme();
            if (now != last)
            {
                last = now;
                onChanged(now);
            }

            if (ops.WaitForChangeOrStop())
            {
                return;
            }
        }
    }
}
