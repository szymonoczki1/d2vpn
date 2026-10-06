using System.Globalization;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Macros;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// One macro preset's settings, shown under its own module panel.
/// </summary>
/// <remarks>
/// <para>
/// One of these per preset, because a preset owns everything about a run - its rotation, its
/// timings, what it drives. Two swaps worth saving separately are usually different in more than
/// which loadouts they touch, so settings shared across presets would mean re-typing them on every
/// switch.
/// </para>
/// <para>
/// Hung off the module rather than given a page in Settings, because these are what you change
/// while tuning a swap that is not working, and walking to another page between attempts is how you
/// lose track of which change did what.
/// </para>
/// <para>
/// Nothing takes effect mid-run: the settings are read when the key is pressed, so a run in
/// progress finishes the way it started.
/// </para>
/// </remarks>
public sealed class MacroSettingsViewModel : ObservableObject
{
    private readonly Func<MacroPreset> _read;
    private readonly Action<MacroPreset> _write;
    private readonly LoadoutGrid _grid;

    /// <summary>Creates the panel over one preset.</summary>
    /// <param name="read">The preset as it stands, read fresh so a save elsewhere is picked up.</param>
    /// <param name="write">Called with every change, to apply and save it.</param>
    /// <param name="grid">The shared screen, for checking slots are on it.</param>
    public MacroSettingsViewModel(
        Func<MacroPreset> read, Action<MacroPreset> write, LoadoutGrid grid)
    {
        _read = read;
        _write = write;
        _grid = grid;
    }

    private MacroPreset Preset => _read();

    /// <summary>What this preset is called, which is also its heading.</summary>
    public string PresetName
    {
        get => Preset.Name;
        set => Change(Preset with
        {
            Name = string.IsNullOrWhiteSpace(value) ? "preset" : value.Trim()
        });
    }

    // ---- timing ----

    /// <summary>
    /// The gap between loadout clicks, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Typed rather than only nudged. The useful range here is single milliseconds - the whole
    /// point of the module is that it is fast - and stepping to 7 from 30 in fives is four clicks
    /// and cannot reach 7 at all.
    /// </remarks>
    public int ClickDelayMs
    {
        get => Preset.ClickDelayMs;
        set => Change(Preset with { ClickDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>How long the swap runs, in milliseconds.</summary>
    public int DurationMs
    {
        get => Preset.DurationMs;
        set => Change(Preset with { DurationMs = Math.Clamp(value, 1, 600000) });
    }

    /// <summary>The run length in seconds, for reading rather than setting.</summary>
    public string DurationLabel =>
        (DurationMs / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + "s";

    /// <summary>How long to wait after F1 for the character screen.</summary>
    public int OpenDelayMs
    {
        get => Preset.OpenDelayMs;
        set => Change(Preset with { OpenDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>How long to wait after the left arrow for the loadout page.</summary>
    public int PageDelayMs
    {
        get => Preset.PageDelayMs;
        set => Change(Preset with { PageDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>
    /// Roughly how many swaps the current settings produce.
    /// </summary>
    /// <remarks>
    /// The number that actually matters, and the one neither setting shows on its own. Halving the
    /// delay or doubling the duration both double it, and seeing that before a run beats counting
    /// it afterwards.
    /// </remarks>
    public string SwapEstimate
    {
        get
        {
            var swaps = Math.Max(1, DurationMs / Math.Max(1, ClickDelayMs + 1));
            return $"about {swaps} swaps at {ClickDelayMs} ms over {DurationLabel}";
        }
    }

    /// <summary>
    /// A warning when the gap is short enough that the game may not see every click.
    /// </summary>
    /// <remarks>
    /// Not a limit, because what the game accepts is a measurement nobody here has made - and
    /// capping it at a guessed number would be worse than saying so. Below about 10 ms the clicks
    /// are arriving faster than a 60 Hz frame, so some of them land on the same frame as the last.
    /// </remarks>
    public string TimingWarning => ClickDelayMs switch
    {
        0 => "0 ms sends clicks as fast as the loop runs. Almost certainly faster than the game reads them.",
        < 10 => "Under 10 ms is faster than one frame at 60 Hz, so some clicks may land unseen.",
        _ => string.Empty
    };

    // ---- what it drives ----

    /// <summary>Hold 3074's uplink for the length of the run.</summary>
    public bool TickUplink
    {
        get => Preset.TickUplink;
        set => Change(Preset with { TickUplink = value });
    }

    /// <summary>Turn 3074's uplink buffer off for the length of the run.</summary>
    public bool DisableBuffer
    {
        get => Preset.DisableBuffer;
        set => Change(Preset with { DisableBuffer = value });
    }

    /// <summary>Press F1 again at the end, closing the character screen.</summary>
    public bool CloseAfter
    {
        get => Preset.CloseAfter;
        set => Change(Preset with { CloseAfter = value });
    }

    /// <summary>
    /// How long to pause before the finishing click.
    /// </summary>
    /// <remarks>
    /// So the rotation has stopped before the click that decides the outcome. Out of a stream
    /// going every few milliseconds, the finishing swap is as droppable as any other.
    /// </remarks>
    public int EndDelayMs
    {
        get => Preset.EndDelayMs;
        set => Change(Preset with { EndDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>
    /// How long to leave the character screen up after the finishing click.
    /// </summary>
    /// <remarks>
    /// Separate from the click gap, and much longer. A loadout applies over several frames, so
    /// closing the screen straight after the click lost the last swap - the one that decides what
    /// you are left wearing.
    /// </remarks>
    public int CloseDelayMs
    {
        get => Preset.CloseDelayMs;
        set => Change(Preset with { CloseDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>How long 3074's uplink stays held after the clicking stops.</summary>
    public int UplinkAfterMs
    {
        get => Preset.UplinkAfterMs;
        set => Change(Preset with { UplinkAfterMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>What the linger means in words, since 0 does not read as "straight away".</summary>
    public string UplinkAfterLabel => UplinkAfterMs == 0
        ? "released as soon as the clicking stops"
        : $"held {UplinkAfterMs} ms past the last click";

    // ---- the rotation ----

    /// <summary>
    /// The rotation, as the numbers typed in order.
    /// </summary>
    /// <remarks>
    /// Text rather than a list control. The order is the whole point and it repeats, so "1, 2, 1,
    /// 3" has to be expressible - which a set of checkboxes cannot say and a reorderable list makes
    /// laborious.
    /// </remarks>
    public string OrderText
    {
        get => string.Join(", ", Preset.Order);
        set
        {
            if (Parse(value) is not { } order)
            {
                // Refused rather than half-applied. Raising puts the old text back in the box,
                // which is the only way the person can see it did not take.
                Raise(nameof(OrderText));
                Raise(nameof(OrderProblem));
                return;
            }

            Change(Preset with { Order = order });
        }
    }

    /// <summary>What is wrong with the rotation, or empty if nothing is.</summary>
    public string OrderProblem => Preset.Order.Count == 0
        ? "no loadouts, so it would do nothing"
        : string.Empty;

    /// <summary>The slot to finish on, or 0 for none.</summary>
    /// <remarks>
    /// Zero rather than a separate switch, because "none" is a real answer here - a rotation that
    /// should stop wherever it got to - and a number box that already has twenty valid values can
    /// carry a twenty-first.
    /// </remarks>
    public int EndOn
    {
        get => Preset.EndOn ?? 0;
        set
        {
            var slot = Math.Clamp(value, 0, _grid.Count);
            Change(Preset with { EndOn = slot == 0 ? null : slot });
        }
    }

    /// <summary>The finishing slot in words, since 0 does not read as "none".</summary>
    public string EndOnLabel => Preset.EndOn is { } slot
        ? $"ends on loadout {slot}"
        : "stops wherever the rotation ended";

    /// <summary>Nudges a value by name, for the arrow buttons.</summary>
    public void Nudge(string what, int by)
    {
        switch (what)
        {
            case "click": ClickDelayMs += by; break;
            case "duration": DurationMs += by; break;
            case "open": OpenDelayMs += by; break;
            case "page": PageDelayMs += by; break;
            case "end-delay": EndDelayMs += by; break;
            case "close": CloseDelayMs += by; break;
            case "uplink": UplinkAfterMs += by; break;
            case "end": EndOn += by; break;
        }
    }

    /// <summary>Re-reads everything, for when the preset changed from elsewhere.</summary>
    public void Refresh() => RaiseEverything();

    /// <summary>
    /// Reads a rotation typed as numbers.
    /// </summary>
    /// <remarks>
    /// Commas or spaces, because both are what people type and neither is worth a complaint.
    /// Returns null for anything that is not a list of slots on the grid, which leaves the old
    /// rotation in place rather than clearing it over a typo.
    /// </remarks>
    private IReadOnlyList<int>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        List<int> order = [];

        foreach (var part in text.Split(
            [',', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out var slot)
                || !_grid.Holds(slot))
            {
                return null;
            }

            order.Add(slot);
        }

        return order.Count == 0 ? null : order;
    }

    private void Change(MacroPreset updated)
    {
        if (updated == Preset)
        {
            return;
        }

        _write(updated);
        RaiseEverything();
    }

    /// <remarks>
    /// Everything, every time. A clamped value has to be pushed back or the box keeps showing what
    /// was typed, and working out which properties one edit could have moved is a list that goes
    /// stale the moment a setting is added.
    /// </remarks>
    private void RaiseEverything() => RaiseAll(
        nameof(PresetName), nameof(ClickDelayMs), nameof(DurationMs), nameof(DurationLabel),
        nameof(OpenDelayMs), nameof(PageDelayMs), nameof(SwapEstimate), nameof(TimingWarning),
        nameof(TickUplink), nameof(DisableBuffer), nameof(CloseAfter),
        nameof(EndDelayMs), nameof(CloseDelayMs),
        nameof(UplinkAfterMs), nameof(UplinkAfterLabel),
        nameof(OrderText), nameof(OrderProblem), nameof(EndOn), nameof(EndOnLabel));
}
