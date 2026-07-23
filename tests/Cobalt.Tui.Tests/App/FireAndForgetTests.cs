using Cobalt.Tui.App;

namespace Cobalt.Tui.Tests.App;

/// <summary>
/// The shared fire-and-forget guard for discarded UI actions (ADR 0013): unexpected
/// faults are recorded AND surfaced to the user, while user cancellation stays silent —
/// and nothing ever escapes as an unobserved task exception.
/// </summary>
public class FireAndForgetTests
{
    [Fact]
    public async Task Unexpected_Fault_Is_Recorded_And_Reported_Not_Rethrown()
    {
        string? reported = null;
        Exception? recorded = null;

        // Must complete (not rethrow) — the caller discards this task.
        await FireAndForget.Observe(
            Task.FromException(new InvalidOperationException("boom")),
            report: msg => reported = msg,
            record: ex => recorded = ex,
            post: a => a());

        Assert.NotNull(recorded);
        Assert.IsType<InvalidOperationException>(recorded);
        Assert.NotNull(reported);
        Assert.Contains("boom", reported);
    }

    [Fact]
    public async Task Cancellation_Is_Silent()
    {
        var reported = false;
        var recorded = false;

        await FireAndForget.Observe(
            Task.FromCanceled(new CancellationToken(canceled: true)),
            report: _ => reported = true,
            record: _ => recorded = true,
            post: a => a());

        Assert.False(reported);
        Assert.False(recorded);
    }

    [Fact]
    public async Task Success_Reports_Nothing()
    {
        var reported = false;

        await FireAndForget.Observe(
            Task.CompletedTask,
            report: _ => reported = true,
            record: _ => reported = true,
            post: a => a());

        Assert.False(reported);
    }

    [Fact]
    public async Task Public_Overload_Routes_The_Report_Through_IUiPost()
    {
        // The production entry point marshals the user-facing report through IUiPost (M2), not
        // inline: a faulting task records nothing to the message bar until the UI queue drains.
        string? reported = null;
        var post = new RecordingUiPost();

        await FireAndForget.Observe(
            Task.FromException(new InvalidOperationException("kaboom")),
            post,
            msg => reported = msg);

        // The fault was observed (task completed, not rethrown) but the report is still queued.
        Assert.Null(reported);
        Assert.Single(post.Posted);

        post.RunAll();

        Assert.NotNull(reported);
        Assert.Contains("kaboom", reported);
    }
}
