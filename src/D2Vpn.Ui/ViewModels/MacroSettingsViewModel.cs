using System.Globalization;

using D2Vpn.Core.Hosting;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The loadout macro's settings, shown under its module panel.
/// </summary>
/// <remarks>
/// <para>
/// Hung off the module rather than given a page in Settings, because every one of these is a thing
/// you change while tuning a swap that is not working - and walking to another page between
/// attempts is how you lose track of which change did what.
/// </para>
/// <para>
/// Every change is applied and written at once, as everywhere else in this window. Nothing takes
/// effect mid-run: the settings are read when the key is pressed, so a run in progress finishes the
/// way it started.
/// </para>
/// </remarks>
public sealed class MacroSettingsViewModel : ObservableObject
{
    private readonly Action<MacroConfig> _apply;
    private MacroConfig _config;

    /// <summary>Creates the panel over the current settings.</summary>
    public MacroSettingsViewModel(MacroConfig config, Action<MacroConfig> apply)
    {
        _config = config;
        _apply = apply;
    }

    /// <summary>The settings as they stand.</summary>
    public MacroConfig Config => _config;

    // ---- timing ----

    /// <summary>The gap between loadout clicks, in milliseconds.</summary>
    public int ClickDelayMs
    {
        get => _config.ClickDelayMs;
        set => Change(_config with { ClickDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>How long the swap runs, in milliseconds.</summary>
    public int DurationMs
    {
        get => _config.DurationMs;
        set => Change(_config with { DurationMs = Math.Clamp(value, 1, 600000) });
    }

    /// <summary>The run length in seconds, for reading rather than setting.</summary>
    public string DurationLabel =>
        (DurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>How long to wait after F1 for the character screen.</summary>
    public int OpenDelayMs
    {
        get => _config.OpenDelayMs;
        set => Change(_config with { OpenDelayMs = Math.Clamp(value, 0, 10000) });
    }

    /// <summary>How long to wait after the left arrow for the loadout page.</summary>
    public int PageDelayMs
    {
        get => _config.PageDelayMs;
        set => Change(_config with { PageDelayMs = Math.Clamp(value, 0, 10000) });
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
            var step = ClickDelayMs + 1;
            var swaps = Math.Max(1, DurationMs / step);

            return $"about {swaps} swaps at {ClickDelayMs} ms over {DurationLabel}";
        }
    }

    // ---- what it drives ----

    /// <summary>Hold 3074's uplink for the length of the run.</summary>
    public bool TickUplink
    {
        get => _config.TickUplink;
        set => Change(_config with { TickUplink = value });
    }

    /// <summary>Turn 3074's uplink buffer off for the length of the run.</summary>
    public bool DisableBuffer
    {
        get => _config.DisableBuffer;
        set => Change(_config with { DisableBuffer = value });
    }

    // ---- presets ----

    /// <summary>The saved rotations, for the selector.</summary>
    public IReadOnlyList<MacroPreset> Presets => _config.Presets;

    /// <summary>Which preset the key runs.</summary>
    public int SelectedPreset
    {
        get => Math.Clamp(_config.Preset, 0, Math.Max(0, _config.Presets.Count - 1));
        set => Change(_config with { Preset = value });
    }

    /// <summary>The selected preset's name.</summary>
    public string PresetName
    {
        get => Selected.Name;
        set => Replace(Selected with
        {
            Name = string.IsNullOrWhiteSpace(value) ? "preset" : value.Trim()
        });
    }

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
        get => string.Join(", ", Selected.Order);
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

            Replace(Selected with { Order = order });
        }
    }

    /// <summary>What is wrong with the rotation, or empty if nothing is.</summary>
    public string OrderProblem => Selected.Order.Count == 0
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
        get => Selected.EndOn ?? 0;
        set
        {
            var slot = Math.Clamp(value, 0, _config.Grid.Count);
            Replace(Selected with { EndOn = slot == 0 ? null : slot });
        }
    }

    /// <summary>The finishing slot in words, since 0 does not read as "none".</summary>
    public string EndOnLabel => Selected.EndOn is { } slot
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
            case "end": EndOn += by; break;
        }
    }

    private MacroPreset Selected => _config.Presets.Count == 0
        ? new MacroPreset()
        : _config.Presets[SelectedPreset];

    /// <summary>Puts an edited preset back into the list.</summary>
    private void Replace(MacroPreset preset)
    {
        if (_config.Presets.Count == 0)
        {
            Change(_config with { Presets = [preset], Preset = 0 });
            return;
        }

        var presets = _config.Presets.ToList();
        presets[SelectedPreset] = preset;

        Change(_config with { Presets = presets });
    }

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
                || !_config.Grid.Holds(slot))
            {
                return null;
            }

            order.Add(slot);
        }

        return order.Count == 0 ? null : order;
    }

    private void Change(MacroConfig updated)
    {
        if (updated == _config)
        {
            return;
        }

        _config = updated;
        _apply(updated);

        RaiseAll(
            nameof(Config), nameof(ClickDelayMs), nameof(DurationMs), nameof(DurationLabel),
            nameof(OpenDelayMs), nameof(PageDelayMs), nameof(SwapEstimate),
            nameof(TickUplink), nameof(DisableBuffer), nameof(Presets), nameof(SelectedPreset),
            nameof(PresetName), nameof(OrderText), nameof(OrderProblem), nameof(EndOn),
            nameof(EndOnLabel));
    }
}
