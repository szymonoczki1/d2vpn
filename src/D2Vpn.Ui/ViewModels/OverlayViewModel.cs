using System.Collections.ObjectModel;
using System.Windows.Media;

using D2Vpn.Core.Hosting;

namespace D2Vpn.Ui.ViewModels;

/// <summary>One row of the overlay.</summary>
/// <param name="Name">The module.</param>
/// <param name="Held">How long it has been on, or empty.</param>
/// <param name="Dot">The colour of its indicator.</param>
public readonly record struct OverlayLine(string Name, string Held, Brush Dot);

/// <summary>
/// What the overlay shows: the modules that are on, and for how long.
/// </summary>
/// <remarks>
/// <para>
/// Rebuilt from the same <see cref="ModuleViewModel"/>s the window binds to, so the two cannot
/// disagree about what is held. It is driven by the window's fast tick for the same reason the
/// window's own timers are - a figure in tenths has to be re-read faster than tenths to read as
/// counting.
/// </para>
/// <para>
/// The collection is replaced only when the text actually changes. An overlay sits over a game, and
/// rebuilding a list forty times a second to produce identical rows is work done on top of
/// something that wants the frame budget.
/// </para>
/// </remarks>
public sealed class OverlayViewModel : ObservableObject
{
    private readonly IReadOnlyList<ModuleViewModel> _modules;
    private OverlayConfig _config;
    private string _signature = string.Empty;

    /// <summary>Creates the overlay's model over the hosted modules.</summary>
    public OverlayViewModel(IReadOnlyList<ModuleViewModel> modules, OverlayConfig config)
    {
        _modules = modules;
        _config = config;
    }

    /// <summary>The rows, in host order.</summary>
    public ObservableCollection<OverlayLine> Lines { get; } = [];

    /// <summary>True when there is anything to show.</summary>
    public bool HasLines => Lines.Count > 0;

    /// <summary>Applies changed settings.</summary>
    public void Apply(OverlayConfig config)
    {
        _config = config;
        _signature = string.Empty;
        Refresh();
    }

    /// <summary>Rebuilds the rows if what they would say has changed.</summary>
    public void Refresh()
    {
        var wanted = _modules
            .Where(m => m.IsEnabled && (m.IsActive || _config.ShowIdle))
            .Where(m => m.Kind != ModuleKind.Trigger)
            .ToList();

        var signature = string.Join(
            "|", wanted.Select(m => $"{m.Name}:{(m.IsActive ? m.HeldLabel : "idle")}"));

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;

        Lines.Clear();

        foreach (var module in wanted)
        {
            Lines.Add(new OverlayLine(
                module.Name,
                _config.ShowTimers && module.IsActive ? module.HeldLabel : string.Empty,
                module.IsActive ? Holding : Idle));
        }

        Raise(nameof(HasLines));
        Raise(nameof(Lines));
    }

    // Frozen, so they are shareable and cheap: an unfrozen brush carries change notification that
    // nothing here needs and that would be re-evaluated on every rebuild.
    private static readonly Brush Holding = Freeze(Color.FromRgb(0xFF, 0x54, 0x70));

    private static readonly Brush Idle = Freeze(Color.FromRgb(0x5C, 0x6A, 0x91));

    private static Brush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
