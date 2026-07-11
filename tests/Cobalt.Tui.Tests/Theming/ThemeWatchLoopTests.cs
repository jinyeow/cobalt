using Cobalt.Tui.Theming;

namespace Cobalt.Tui.Tests.Theming;

/// <summary>
/// Drives <see cref="ThemeWatchLoop.Run"/> through a scripted fake so the two behaviours that the
/// real Windows registry seam can't unit-test — arm-before-read (no lost change) and
/// retry-instead-of-park — are pinned deterministically on every OS.
/// </summary>
public class ThemeWatchLoopTests
{
    [Fact]
    public void Reads_The_Theme_Only_After_Arming_And_Fires_Only_On_A_Real_Change()
    {
        // baseline Dark, then: (no change) → Light → (spurious, no change) → Dark, then stop.
        var ops = new FakeWatchOps(
            reads: [OsTheme.Dark, OsTheme.Dark, OsTheme.Light, OsTheme.Light, OsTheme.Dark],
            arms: [true, true, true, true],
            waits: [false, false, false, true]);
        var fired = new List<OsTheme>();

        ThemeWatchLoop.Run(ops, stopped: () => false, onChanged: fired.Add);

        // C5: de-duped — only the two genuine transitions fire, not the spurious wake.
        Assert.Equal(new[] { OsTheme.Light, OsTheme.Dark }, fired);

        // C1: every notifying read (all but the baseline) is immediately preceded by an arm, so a
        // write racing the read is caught by the already-armed notification instead of being lost.
        for (var i = 1; i < ops.Ops.Count; i++)
        {
            if (ops.Ops[i] == "read")
            {
                Assert.Equal("arm", ops.Ops[i - 1]);
            }
        }
    }

    [Fact]
    public void Retries_With_Backoff_When_Arming_Fails_Then_Recovers()
    {
        // arm fails twice, backs off each time, then succeeds and observes the change.
        var ops = new FakeWatchOps(
            reads: [OsTheme.Dark, OsTheme.Light],
            arms: [false, false, true],
            waits: [true],
            backoffs: [false, false]);
        var fired = new List<OsTheme>();

        ThemeWatchLoop.Run(ops, stopped: () => false, onChanged: fired.Add);

        // C2: a transient arm failure does not park forever — it backs off, retries, and recovers.
        Assert.Equal(2, ops.BackoffCount);
        Assert.Equal(new[] { OsTheme.Light }, fired);
    }

    [Fact]
    public void Stop_During_Backoff_Exits_Without_Firing()
    {
        var ops = new FakeWatchOps(
            reads: [OsTheme.Dark],
            arms: [false],
            waits: [],
            backoffs: [true]);
        var fired = new List<OsTheme>();

        ThemeWatchLoop.Run(ops, stopped: () => false, onChanged: fired.Add);

        Assert.Equal(1, ops.ArmCount);
        Assert.Equal(1, ops.BackoffCount);
        Assert.Empty(fired);
    }

    [Fact]
    public void Stop_Signalled_Before_The_First_Arm_Never_Arms()
    {
        var ops = new FakeWatchOps(reads: [OsTheme.Dark], arms: [], waits: []);
        var fired = new List<OsTheme>();

        ThemeWatchLoop.Run(ops, stopped: () => true, onChanged: fired.Add);

        Assert.Equal(0, ops.ArmCount);
        Assert.Empty(fired);
    }

    private sealed class FakeWatchOps(
        IEnumerable<OsTheme> reads,
        IEnumerable<bool> arms,
        IEnumerable<bool> waits,
        IEnumerable<bool>? backoffs = null) : IThemeWatchOps
    {
        private readonly Queue<OsTheme> _reads = new(reads);
        private readonly Queue<bool> _arms = new(arms);
        private readonly Queue<bool> _waits = new(waits);
        private readonly Queue<bool> _backoffs = new(backoffs ?? []);

        public List<string> Ops { get; } = [];
        public int ArmCount { get; private set; }
        public int BackoffCount { get; private set; }

        public OsTheme ReadTheme()
        {
            Ops.Add("read");
            return _reads.Dequeue();
        }

        public bool TryArm()
        {
            Ops.Add("arm");
            ArmCount++;
            return _arms.Dequeue();
        }

        public bool WaitForChangeOrStop()
        {
            Ops.Add("wait");
            return _waits.Dequeue();
        }

        public bool BackoffOrStop()
        {
            Ops.Add("backoff");
            BackoffCount++;
            return _backoffs.Dequeue();
        }

        public void Dispose()
        {
        }
    }
}
