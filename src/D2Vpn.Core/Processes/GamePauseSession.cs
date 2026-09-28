namespace D2Vpn.Core.Processes;

/// <summary>Why a pause ended.</summary>
public enum ResumeReason
{
    /// <summary>The operator pressed the key again.</summary>
    Toggle,

    /// <summary>The safety timeout expired and the pause was ended for them.</summary>
    SafetyTimeout,

    /// <summary>The session is shutting down.</summary>
    Shutdown,

    /// <summary>The process exited while it was paused.</summary>
    ProcessGone
}

/// <summary>
/// Pauses the game by suspending it, and guarantees it is let go again.
/// </summary>
/// <remarks>
/// <para>
/// The most total hold in this project, and the only one that is not about packets. Every other
/// module leaves the game running and takes its traffic away; this stops the process itself, so
/// nothing is sent, nothing is received, nothing is drawn and nothing is simulated. To the peers
/// it looks like an uplink and downlink hold at once, which is the effect. To the operator it
/// looks like a frozen window, which is the risk.
/// </para>
/// <para>
/// <b>The failure modes are not symmetrical with the other modules.</b> A traffic hold that goes
/// wrong produces an error code and the game reconnects by itself. A suspend that goes wrong
/// leaves a process that will never run again, and the only way out is killing the game and
/// losing the session. So this type is built around letting go rather than around holding on:
/// </para>
/// <list type="bullet">
/// <item>a safety timeout is <b>on by default</b>, unlike every hold here, where the operator
/// decides the length;</item>
/// <item><see cref="Dispose"/> resumes if a pause is in progress, so any exit path that runs
/// finalisation releases the game;</item>
/// <item>a process that exits while paused is noticed rather than resumed into.</item>
/// </list>
/// <para>
/// What cannot be defended against is this process being killed outright - Task Manager, a power
/// loss - while a pause is open. There is no cleanup path there, and the game stays frozen. That
/// is the reason the safety timeout defaults on and is worth keeping short.
/// </para>
/// <para>
/// Time is passed in rather than read from the clock, so the timeout can be tested without
/// waiting for it.
/// </para>
/// </remarks>
public sealed class GamePauseSession : IDisposable
{
    private readonly IProcessControl _process;
    private readonly TimeSpan? _safetyTimeout;

    private DateTimeOffset _pausedAt;
    private bool _disposed;

    /// <summary>Creates a session over a process.</summary>
    /// <param name="process">The process to suspend.</param>
    /// <param name="safetyTimeout">
    /// How long a pause may run before it is ended automatically, or <see langword="null"/> for
    /// no limit. Null is a deliberate choice to accept a frozen game if this process dies.
    /// </param>
    public GamePauseSession(IProcessControl process, TimeSpan? safetyTimeout)
    {
        _process = process;
        _safetyTimeout = safetyTimeout;
    }

    /// <summary>True while the game is suspended.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>How many pauses have been started.</summary>
    public int Pauses { get; private set; }

    /// <summary>Total time spent paused, across the session.</summary>
    public TimeSpan TotalPaused { get; private set; }

    /// <summary>The longest single pause.</summary>
    public TimeSpan LongestPause { get; private set; }

    /// <summary>How many pauses the safety timeout had to end.</summary>
    public int SafetyStops { get; private set; }

    /// <summary>The process being paused.</summary>
    public string ProcessName => _process.Name;

    /// <summary>The safety timeout in force, if any.</summary>
    public TimeSpan? SafetyTimeout => _safetyTimeout;

    /// <summary>Raised when the game is suspended.</summary>
    public event Action<DateTimeOffset>? Paused;

    /// <summary>Raised when it is let go, with how long it was held and why.</summary>
    public event Action<TimeSpan, ResumeReason>? Resumed;

    /// <summary>Pauses if running, resumes if paused.</summary>
    /// <returns>True if the game is paused afterwards.</returns>
    public bool Toggle(DateTimeOffset now)
    {
        if (IsPaused)
        {
            Release(now, ResumeReason.Toggle);
            return false;
        }

        Pause(now);
        return IsPaused;
    }

    /// <summary>Suspends the game, if it is not already suspended.</summary>
    public void Pause(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsPaused)
        {
            return;
        }

        if (!_process.IsAlive)
        {
            throw new InvalidOperationException($"{_process.Name} is no longer running");
        }

        _process.Suspend();

        IsPaused = true;
        _pausedAt = now;
        Pauses++;

        Paused?.Invoke(now);
    }

    /// <summary>Resumes the game, if it is paused.</summary>
    public void Release(DateTimeOffset now, ResumeReason reason)
    {
        if (!IsPaused)
        {
            return;
        }

        // Clear the flag first. If the resume throws, a second attempt from Dispose would throw
        // the same way, and the useful behaviour is to report once and carry on shutting down.
        IsPaused = false;

        var held = now - _pausedAt;
        TotalPaused += held;

        if (held > LongestPause)
        {
            LongestPause = held;
        }

        if (reason == ResumeReason.SafetyTimeout)
        {
            SafetyStops++;
        }

        // A process that has exited cannot be resumed, and asking ntdll to do it reports a
        // failure that reads like a bug in the pause rather than a game that closed.
        if (_process.IsAlive)
        {
            _process.Resume();
        }
        else
        {
            reason = ResumeReason.ProcessGone;
        }

        Resumed?.Invoke(held, reason);
    }

    /// <summary>
    /// Ends a pause that has outlived the safety timeout, or that the game exited during.
    /// </summary>
    /// <remarks>Called on a timer by whatever is driving the session.</remarks>
    /// <returns>True if this call ended a pause.</returns>
    public bool Tick(DateTimeOffset now)
    {
        if (!IsPaused)
        {
            return false;
        }

        if (!_process.IsAlive)
        {
            Release(now, ResumeReason.ProcessGone);
            return true;
        }

        if (_safetyTimeout is { } limit && now - _pausedAt >= limit)
        {
            Release(now, ResumeReason.SafetyTimeout);
            return true;
        }

        return false;
    }

    /// <summary>How long the current pause has been running, or zero if not paused.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => IsPaused ? now - _pausedAt : TimeSpan.Zero;

    /// <summary>Resumes the game if it is paused, then releases the handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Release(DateTimeOffset.UtcNow, ResumeReason.Shutdown);
        }
        catch (Exception ex)
        {
            // Nothing above this can do anything useful with it, and swallowing it silently would
            // hide the one failure that leaves the game frozen.
            Console.Error.WriteLine($"WARNING: could not resume {_process.Name}: {ex.Message}");
            Console.Error.WriteLine(
                "The game may still be suspended. Close it from Task Manager if it is frozen.");
        }

        _process.Dispose();
    }
}
