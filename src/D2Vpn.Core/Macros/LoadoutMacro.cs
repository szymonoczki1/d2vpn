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

    /// <summary>Clicks the loadout the run is meant to end on.</summary>
    private int Finish(MacroPlan plan, CancellationToken token)
    {
        if (plan.EndOn is not { } slot)
        {
            return Swaps;
        }

        _clock.Wait(plan.ClickDelay, token);

        var (x, y) = plan.Grid.PointFor(slot);
        _input.ClickAt(x, y);
        Swaps++;

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
public readonly record struct MacroPlan(
    LoadoutGrid Grid,
    IReadOnlyList<int> Order,
    int? EndOn,
    TimeSpan Duration,
    TimeSpan ClickDelay,
    TimeSpan OpenDelay,
    TimeSpan PageDelay)
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
