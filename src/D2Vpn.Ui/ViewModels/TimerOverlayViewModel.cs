using System.Windows.Media;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Network;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The instance timer, as a row of the overlay.
/// </summary>
/// <remarks>
/// <para>
/// Shares <see cref="OverlayViewModel"/>'s shape rather than having a panel of its own, so the two
/// can be drawn by the same window and can be stacked into one panel when attached. A row is a
/// name and a figure either way.
/// </para>
/// <para>
/// Nothing is shown while the instance is dormant. The timer keeps running behind a loading screen,
/// but a figure frozen on screen for the length of one would be read as the thing having hung.
/// </para>
/// </remarks>
public sealed class TimerOverlayViewModel : OverlayContent
{
    private readonly InstanceWatcher _watcher;
    private TimerConfig _config;

    // Nullable, and null is the sentinel for "rebuild whatever it says". Empty string cannot be:
    // that is also what the signature of a panel showing nothing looks like, so turning the timer
    // off set the sentinel, found it equal to the new empty text, and returned without clearing -
    // leaving the last figure frozen on screen for good.
    private string? _signature;

    /// <summary>Creates the timer's overlay content.</summary>
    public TimerOverlayViewModel(InstanceWatcher watcher, TimerConfig config)
    {
        _watcher = watcher;
        _config = config;
    }

    /// <summary>Applies changed settings.</summary>
    public void Apply(TimerConfig config)
    {
        _config = config;
        _signature = null;
        Refresh();
    }

    /// <inheritdoc/>
    public override void Refresh()
    {
        var now = DateTimeOffset.UtcNow;

        var text = _config.Enabled && _watcher.ShouldShow(now)
            ? Format(_watcher.Elapsed(now))
            : string.Empty;

        if (text == _signature)
        {
            return;
        }

        _signature = text;
        Lines.Clear();

        if (text.Length > 0)
        {
            Lines.Add(new OverlayLine("instance", text, Accent));
        }

        Raise(nameof(HasLines));
        Raise(nameof(Lines));
    }

    /// <summary>
    /// Minutes and seconds, because an instance lasts long enough for tenths to be noise.
    /// </summary>
    /// <remarks>
    /// Unlike a hold, which is measured in seconds and read while it runs. An activity is measured
    /// in minutes, and a figure whose last digit changes ten times a second is harder to read at a
    /// glance than one that does not.
    /// </remarks>
    private static string Format(TimeSpan held) => held.TotalHours >= 1
        ? $"{(int)held.TotalHours}:{held.Minutes:00}:{held.Seconds:00}"
        : $"{(int)held.TotalMinutes}:{held.Seconds:00}";

    private static readonly Brush Accent = Freeze(Color.FromRgb(0x3D, 0x7B, 0xFF));

    private static Brush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
