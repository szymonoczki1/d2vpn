using D2Vpn.Core.Processes;

namespace D2Vpn.Core.Tests.Processes;

/// <summary>
/// The pause policy, tested without suspending anything.
/// </summary>
/// <remarks>
/// Everything here is about letting go. A pause that fails to end is the one failure this module
/// has that the traffic modules do not - they produce an error code and recover, this leaves a
/// process that will never run again - so the release paths carry the tests.
/// </remarks>
public class GamePauseTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Toggling_pauses_then_resumes()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, null);

        Assert.True(session.Toggle(T0));
        Assert.True(session.IsPaused);
        Assert.Equal(1, process.Suspends);

        Assert.False(session.Toggle(T0.AddSeconds(3)));
        Assert.False(session.IsPaused);
        Assert.Equal(1, process.Resumes);
        Assert.Equal(TimeSpan.FromSeconds(3), session.TotalPaused);
    }

    [Fact]
    public void Pausing_twice_suspends_once()
    {
        // The suspend count in Windows is a depth, not a flag: suspending twice would need
        // resuming twice, and a single resume on the way out would leave the game frozen.
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, null);

        session.Pause(T0);
        session.Pause(T0.AddSeconds(1));

        Assert.Equal(1, process.Suspends);
        Assert.Equal(1, session.Pauses);
    }

    [Fact]
    public void Resuming_when_not_paused_does_nothing()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, null);

        session.Release(T0, ResumeReason.Toggle);

        Assert.Equal(0, process.Resumes);
        Assert.Equal(0, session.Pauses);
    }

    [Fact]
    public void The_safety_timeout_ends_a_pause_that_outlives_it()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, TimeSpan.FromSeconds(30));

        session.Pause(T0);

        Assert.False(session.Tick(T0.AddSeconds(29)));
        Assert.True(session.IsPaused);

        Assert.True(session.Tick(T0.AddSeconds(30)));
        Assert.False(session.IsPaused);
        Assert.Equal(1, process.Resumes);
        Assert.Equal(1, session.SafetyStops);
    }

    [Fact]
    public void A_null_safety_timeout_never_ends_a_pause()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, null);

        session.Pause(T0);

        Assert.False(session.Tick(T0.AddHours(1)));
        Assert.True(session.IsPaused);
        Assert.Equal(0, process.Resumes);
    }

    [Fact]
    public void Disposing_while_paused_resumes_the_game()
    {
        // The guarantee the whole module rests on: every exit path that runs finalisation lets go.
        var process = new FakeProcess();
        var session = new GamePauseSession(process, null);

        session.Pause(T0);
        session.Dispose();

        Assert.Equal(1, process.Resumes);
        Assert.False(session.IsPaused);
    }

    [Fact]
    public void Disposing_twice_resumes_once()
    {
        var process = new FakeProcess();
        var session = new GamePauseSession(process, null);

        session.Pause(T0);
        session.Dispose();
        session.Dispose();

        Assert.Equal(1, process.Resumes);
    }

    [Fact]
    public void A_failing_resume_does_not_throw_out_of_dispose()
    {
        // Throwing here would replace a warning the operator can act on with a stack trace, on
        // the one path where they most need to be told the game is still frozen.
        var process = new FakeProcess { FailResume = true };
        var session = new GamePauseSession(process, null);

        session.Pause(T0);

        var ex = Record.Exception(session.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void A_process_that_exits_while_paused_is_not_resumed_into()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, TimeSpan.FromSeconds(30));

        session.Pause(T0);
        process.IsAlive = false;

        Assert.True(session.Tick(T0.AddSeconds(1)));
        Assert.Equal(0, process.Resumes);
        Assert.False(session.IsPaused);
    }

    [Fact]
    public void Pausing_a_process_that_has_exited_throws()
    {
        var process = new FakeProcess { IsAlive = false };
        using var session = new GamePauseSession(process, null);

        Assert.Throws<InvalidOperationException>(() => session.Pause(T0));
        Assert.Equal(0, process.Suspends);
    }

    [Fact]
    public void The_longest_pause_is_tracked_across_several()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, null);

        session.Pause(T0);
        session.Release(T0.AddSeconds(2), ResumeReason.Toggle);

        session.Pause(T0.AddSeconds(10));
        session.Release(T0.AddSeconds(17), ResumeReason.Toggle);

        session.Pause(T0.AddSeconds(20));
        session.Release(T0.AddSeconds(23), ResumeReason.Toggle);

        Assert.Equal(3, session.Pauses);
        Assert.Equal(TimeSpan.FromSeconds(12), session.TotalPaused);
        Assert.Equal(TimeSpan.FromSeconds(7), session.LongestPause);
    }

    [Fact]
    public void The_resume_reason_reaches_the_event()
    {
        var process = new FakeProcess();
        using var session = new GamePauseSession(process, TimeSpan.FromSeconds(5));

        var reasons = new List<ResumeReason>();
        session.Resumed += (_, reason) => reasons.Add(reason);

        session.Pause(T0);
        session.Release(T0.AddSeconds(1), ResumeReason.Toggle);

        session.Pause(T0.AddSeconds(10));
        session.Tick(T0.AddSeconds(15));

        Assert.Equal([ResumeReason.Toggle, ResumeReason.SafetyTimeout], reasons);
    }

    private sealed class FakeProcess : IProcessControl
    {
        public string Name => "fake";

        public bool IsAlive { get; set; } = true;

        public bool FailResume { get; init; }

        public int Suspends { get; private set; }

        public int Resumes { get; private set; }

        public void Suspend() => Suspends++;

        public void Resume()
        {
            if (FailResume)
            {
                throw new InvalidOperationException("refused");
            }

            Resumes++;
        }

        public void Dispose()
        {
        }
    }
}
