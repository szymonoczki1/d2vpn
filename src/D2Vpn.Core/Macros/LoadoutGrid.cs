namespace D2Vpn.Core.Macros;

/// <summary>
/// Where each loadout sits on the character screen.
/// </summary>
/// <remarks>
/// <para>
/// A regular grid, so one corner and one pitch describe all twenty: measured at 1920x1080 with
/// Windows scaling at 100%, slot 1 is at (117, 385) and centres are 96 px apart both ways.
/// </para>
/// <para>
/// Every number is settable because none of them is a fact about the game - they are facts about
/// one screen. A different resolution moves all twenty, and the whole point of storing a corner and
/// a pitch rather than twenty pairs is that re-measuring is then two numbers rather than forty.
/// </para>
/// <para>
/// Numbered the way they are read: left to right, then down. Slot 2 is to the right of slot 1 and
/// slot 5 is below it, which is what makes <see cref="Columns"/> rather than rows the wrapping
/// dimension.
/// </para>
/// </remarks>
public sealed record LoadoutGrid
{
    /// <summary>Screen x of the centre of slot 1.</summary>
    public int OriginX { get; init; } = 117;

    /// <summary>Screen y of the centre of slot 1.</summary>
    public int OriginY { get; init; } = 385;

    /// <summary>Distance between neighbouring centres, both ways.</summary>
    public int Pitch { get; init; } = 96;

    /// <summary>How many fit across before wrapping.</summary>
    public int Columns { get; init; } = 4;

    /// <summary>How many rows there are.</summary>
    public int Rows { get; init; } = 5;

    /// <summary>The highest slot number this grid holds.</summary>
    public int Count => Columns * Rows;

    /// <summary>Whether a slot number is one of this grid's.</summary>
    public bool Holds(int slot) => slot >= 1 && slot <= Count;

    /// <summary>
    /// Where to click for a slot, numbered from 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The slot is not in the grid.</exception>
    public (int X, int Y) PointFor(int slot)
    {
        if (!Holds(slot))
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"the grid holds slots 1 to {Count}");
        }

        var index = slot - 1;

        return (
            OriginX + ((index % Columns) * Pitch),
            OriginY + ((index / Columns) * Pitch));
    }

    /// <summary>Checks the grid describes something clickable.</summary>
    public void Validate()
    {
        if (Columns < 1 || Rows < 1)
        {
            throw new ArgumentException(
                $"the loadout grid is {Columns}x{Rows}, which holds nothing");
        }

        if (Pitch < 1)
        {
            throw new ArgumentException(
                $"loadout pitch {Pitch} would put every slot in the same place");
        }

        if (OriginX < 0 || OriginY < 0)
        {
            throw new ArgumentException(
                $"loadout origin ({OriginX}, {OriginY}) is off the left or top of the screen");
        }
    }
}
