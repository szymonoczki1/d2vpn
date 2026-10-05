using System.Diagnostics;

using D2Vpn.Core.Input;

namespace D2Vpn.Core.Macros;

/// <summary>
/// Waiting, and knowing how long has passed.
/// </summary>
/// <remarks>
/// <para>
/// One seam rather than two, because the two are the same fact. A wait that does not advance the
/// clock is not a wait, and a test built from a no-op wait and a real stopwatch races: the macro's
/// duration is then decided by how fast the test machine happens to run the loop.
/// </para>
/// <para>
/// So a fake advances its own clock by whatever it was asked to wait, and a ten-second macro is
/// tested exactly, in no time at all.
/// </para>
/// </remarks>
public interface IMacroClock
{
    /// <summary>Starts timing from zero.</summary>
    void Restart();

    /// <summary>How long since <see cref="Restart"/>.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Waits, returning false if it was cancelled.</summary>
    bool Wait(TimeSpan duration, CancellationToken token);
}

/// <summary>The real one: a stopwatch, and a wait accurate enough to set in milliseconds.</summary>
public sealed class MacroClock : IMacroClock
{
    private readonly Stopwatch _clock = new();

    /// <inheritdoc/>
    public void Restart() => _clock.Restart();

    /// <inheritdoc/>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <inheritdoc/>
    public bool Wait(TimeSpan duration, CancellationToken token) =>
        PreciseWait.Wait(duration, token);
}
