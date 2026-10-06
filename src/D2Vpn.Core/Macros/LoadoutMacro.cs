using D2Vpn.Core.Input;

namespace D2Vpn.Core.Macros;

/// <summary>
/// The loadout swap, as a sequence of clicks.
/// </summary>
/// <remarks>
/// <para>
/// Opens the character screen, pages left to the loadouts, then cycles the chosen slots in order
/// for as long as it is given, and finishes on the one asked for. The finish is not optional
/// bookkeeping: stopping mid-cycle leaves whichever loadout the clock happened to land on, which
/// for a swap done to desync is the difference between ending armed and ending in whatever was
/// third in the list.
/// </para>
/// <para>
/// Nothing here knows about the mouse. It calls an <see cref="IInputSink"/>, which in a test is a
/// recorder - so the order, the coordinates and the waits are all assertable without a game, a
/// screen or a pointer that moves while someone is using the machine.
/// </para>
/// </remarks>
public sealed class LoadoutMacro
{
    private readonly IInputSink _input;
    private readonly IMacroClock _clock;

    /// <summary>Creates a macro over an input sink.</summary>
    /// <param name="input">Where the clicks go.</param>
    /// <param name="clock">
    /// Waiting and elapsed time, which are one seam because they are one fact. A fake advances its
    /// own clock by whatever it was asked to wait, so a ten-second macro is tested exactly and in
    /// no time at all.
    /// </param>
    public LoadoutMacro(IInputSink input, IMacroClock? clock = null)
    {
        _input = input;
        _clock = clock ?? new MacroClock();
    }

    /// <summary>Virtual-key code for F1, which opens the character screen.</summary>
    private const ushort VkF1 = 0x70;

    /// <summary>Virtual-key code for the left arrow, which pages to the loadouts.</summary>
    private const ushort VkLeft = 0x25;

    /// <summary>How many swaps were clicked on the last run.</summary>
    public int Swaps { get; private set; }

    /// <summary>
    /// Runs the swap.
    /// </summary>
    /// <param name="plan">What to click, and for how long.</param>
    /// <param name="token">Stops it early. The finishing loadout is still clicked.</param>
    /// <returns>How many swaps were made.</returns>
    public int Run(MacroPlan plan, CancellationToken token)
    {
        plan.Validate();
        Swaps = 0;

        // F1, a pause for the screen to come up, then left to the loadouts. The pause is a setting
        // because it is really "how long does this machine take to draw that screen", and a click
        // sent before it is there goes to whatever was underneath.
        _input.PressKey(VkF1);

        if (!_clock.Wait(plan.OpenDelay, token))
        {
            return Finish(plan, CancellationToken.None);
        }

        _input.PressKey(VkLeft);

        if (!_clock.Wait(plan.PageDelay, token))
        {
            return Finish(plan, CancellationToken.None);
        }

        // Restarted here rather than at the top: the time spent waiting for the character screen
        // is not time spent swapping, and counting it would make a short run mostly loading.
        _clock.Restart();
        var index = 0;

        while (!token.IsCancellationRequested && _clock.Elapsed < plan.Duration)
        {
            var slot = plan.Order[index % plan.Order.Count];
            index++;

            var (x, y) = plan.Grid.PointFor(slot);
            _input.ClickAt(x, y);
            Swaps++;

            // Checked before the wait as well as after, so the last cycle of a run does not spend
            // its delay on a macro that is already over.
            if (_clock.Elapsed >= plan.Duration)
            {
                break;
            }

            if (!_clock.Wait(plan.ClickDelay, token))
            {
                break;
            }
        }

        // CancellationToken.None: a stop asks the cycling to end, not the loadout to be left
        // wherever it got to.
        return Finish(plan, CancellationToken.None);
    }

    /// <summary>Clicks the loadout the run is meant to end on, then tidies up.</summary>
    /// <remarks>
    /// Both halves run however the macro ended, including on a stop. Leaving the character screen
    /// open after an interrupted run is the state nobody asked for - the next key press goes to a
    /// menu rather than to the game.
    /// </remarks>
    private int Finish(MacroPlan plan, CancellationToken token)
    {
        if (plan.EndOn is { } slot)
        {
            // Its own pause, not a click gap. Clicked straight out of a stream going every few
            // milliseconds, the finishing swap is one more in the queue and as droppable as the
            // rest - and the point of naming a loadout to end on is that this one must land.
            _clock.Wait(plan.EndDelay, token);

            var (x, y) = plan.Grid.PointFor(slot);
            _input.ClickAt(x, y);
            Swaps++;
        }

        if (plan.CloseAfter)
        {
            // The same key that opened it, after its own settle - not after a click gap. A loadout
            // applies over several frames, and closing the screen straight after the click meant
            // the last swap, the one that decides what you are left wearing, never went through.
            _clock.Wait(plan.CloseDelay, token);
            _input.PressKey(VkF1);
        }

        return Swaps;
    }
}

/// <summary>Everything one run of the macro needs.</summary>
/// <param name="Grid">Where the loadouts are.</param>
/// <param name="Order">The slots to cycle, in order. Repeats until the duration is up.</param>
/// <param name="EndOn">The slot to finish on, or null to stop wherever the cycle ended.</param>
/// <param name="Duration">How long to cycle for.</param>
/// <param name="ClickDelay">The gap between clicks.</param>
/// <param name="OpenDelay">How long to wait after F1 for the character screen.</param>
/// <param name="PageDelay">How long to wait after the left arrow for the loadouts.</param>
/// <param name="CloseAfter">Press F1 again at the end, closing the character screen.</param>
/// <param name="CloseDelay">How long to leave the screen up after the finishing click.</param>
/// <param name="EndDelay">How long to pause before the finishing click.</param>
public readonly record struct MacroPlan(
    LoadoutGrid Grid,
    IReadOnlyList<int> Order,
    int? EndOn,
    TimeSpan Duration,
    TimeSpan ClickDelay,
    TimeSpan OpenDelay,
    TimeSpan PageDelay,
    bool CloseAfter = false,
    TimeSpan CloseDelay = default,
    TimeSpan EndDelay = default)
{
    /// <summary>Checks the plan can be run, with a message saying what to change if not.</summary>
    public void Validate()
    {
        Grid.Validate();

        if (Order.Count == 0)
        {
            throw new ArgumentException("the macro has no loadouts to swap between");
        }

        foreach (var slot in Order)
        {
            if (!Grid.Holds(slot))
            {
                throw new ArgumentException(
                    $"loadout {slot} is not on the grid, which holds 1 to {Grid.Count}");
            }
        }

        if (EndOn is { } end && !Grid.Holds(end))
        {
            throw new ArgumentException(
                $"it is set to end on loadout {end}, which is not on the grid");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("the macro has no duration, so it would do nothing");
        }
    }
}
