namespace D2Vpn.Core.Macros;

/// <summary>
/// One run's settings, with the preset already chosen.
/// </summary>
/// <remarks>
/// The runtime shape, separate from the config record that is written to disk. The config holds
/// every preset; this holds the one that is about to run, so nothing downstream has to know that
/// presets exist.
/// </remarks>
public readonly record struct MacroSettings
{
    /// <summary>Where the loadouts are on screen.</summary>
    public LoadoutGrid Grid { get; init; }

    /// <summary>Which uplink hold to drive, by its control-surface name.</summary>
    /// <remarks>
    /// No initialiser: a struct with one needs an explicit constructor, and every caller of this
    /// builds it from a preset which always names an uplink. Null here would mean a lookup for
    /// nothing, which the runner reports as a missing hold.
    /// </remarks>
    public string Uplink { get; init; }

    /// <summary>The slots to cycle, in order.</summary>
    public IReadOnlyList<int> Order { get; init; }

    /// <summary>The slot to finish on, or null to stop wherever the cycle ended.</summary>
    public int? EndOn { get; init; }

    /// <summary>How long to keep swapping.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The gap between clicks.</summary>
    public TimeSpan ClickDelay { get; init; }

    /// <summary>How long to wait after F1 for the character screen to appear.</summary>
    public TimeSpan OpenDelay { get; init; }

    /// <summary>How long to wait after the left arrow for the loadouts to appear.</summary>
    public TimeSpan PageDelay { get; init; }

    /// <summary>Hold 3074's uplink for the length of the run.</summary>
    public bool TickUplink { get; init; }

    /// <summary>Turn 3074's uplink buffer off for the length of the run.</summary>
    public bool DisableBuffer { get; init; }

    /// <summary>Press F1 again at the end, closing the character screen.</summary>
    public bool CloseAfter { get; init; }

    /// <summary>How many times to click the finishing loadout.</summary>
    public int EndClicks { get; init; }

    /// <summary>How long a loadout needs to apply.</summary>
    public TimeSpan Settle { get; init; }

    /// <summary>How long the uplink stays held after the clicking stops.</summary>
    public TimeSpan UplinkAfter { get; init; }

    /// <summary>The plan this describes.</summary>
    /// <remarks>
    /// <see cref="UplinkAfter"/> is not in it. The plan is the click sequence, and what happens to
    /// a traffic hold afterwards is the runner's business - the macro itself knows nothing about
    /// holds.
    /// </remarks>
    public MacroPlan ToPlan() => new(
        Grid, Order, EndOn, Duration, ClickDelay, OpenDelay, PageDelay,
        CloseAfter, Settle, EndClicks);
}
