using System.Diagnostics;

namespace D2Vpn.Core.Input;

/// <summary>
/// Somewhere to send clicks and keystrokes.
/// </summary>
/// <remarks>
/// The seam that lets a macro be tested. A real one moves the pointer; a recording one writes down
/// what it was asked to do, so "it opens the character screen, clicks slot 5 then slot 2, and ends
/// on slot 1" is a test rather than something to watch happen and hope.
/// </remarks>
public interface IInputSink
{
    /// <summary>Moves the pointer to a screen pixel.</summary>
    void MoveTo(int x, int y);

    /// <summary>Moves and left-clicks there.</summary>
    void ClickAt(int x, int y);

    /// <summary>Taps a key, by virtual-key code.</summary>
    void PressKey(ushort virtualKey);
}

/// <summary>
/// Waiting accurately enough to be worth setting in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Thread.Sleep(int)"/> and <c>Task.Delay</c> are bounded by the scheduler's tick, which
/// is about 15 ms by default. Asking either for 5 ms gets something between 5 and 20 - so a macro
/// built on them has a delay setting whose small values all mean the same thing, and whose large
/// values drift.
/// </para>
/// <para>
/// So: sleep for the part that is safely longer than a tick, then spin out the remainder. Spinning
/// burns a core, which is why it is only ever the last few milliseconds.
/// </para>
/// </remarks>
public static class PreciseWait
{
    /// <summary>How much is left to the spin. A tick and a bit, so the sleep cannot overshoot.</summary>
    private static readonly TimeSpan SpinFrom = TimeSpan.FromMilliseconds(16);

    /// <summary>Waits, give or take a fraction of a millisecond.</summary>
    /// <param name="duration">How long to wait. Zero or less returns at once.</param>
    /// <param name="token">Checked while waiting, so a long one can be cut short.</param>
    /// <returns>False if the wait was cancelled.</returns>
    public static bool Wait(TimeSpan duration, CancellationToken token)
    {
        if (duration <= TimeSpan.Zero)
        {
            return !token.IsCancellationRequested;
        }

        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            var left = duration - clock.Elapsed;

            if (left <= TimeSpan.Zero)
            {
                return true;
            }

            if (left > SpinFrom)
            {
                // Chunked rather than slept in one go, so cancelling a ten-second wait does not
                // take ten seconds to notice.
                Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds - 15, 50)));
                continue;
            }

            Thread.SpinWait(200);
        }
    }
}
