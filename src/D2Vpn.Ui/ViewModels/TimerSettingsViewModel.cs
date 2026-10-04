using D2Vpn.Core.Hosting;
using D2Vpn.Core.Network;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The instance timer's settings, as a page in the sidebar.
/// </summary>
/// <remarks>
/// Two waits on silence, and the shorter one only hides. A loading screen stops 30k traffic without
/// ending anything, so the timer keeps running out of sight and picks up where it left off when the
/// same server comes back - a timer that vanished and returned at zero would be worse than one that
/// waits.
/// </remarks>
public sealed class TimerSettingsViewModel : SidebarItem
{
    private readonly Action<TimerConfig> _apply;
    private readonly InstanceWatcher _watcher;
    private readonly bool _fed;
    private TimerConfig _config;

    /// <summary>Creates the page over the current settings.</summary>
    /// <param name="config">The waits as they stand.</param>
    /// <param name="watcher">What is being timed, so the page can show it running.</param>
    /// <param name="fed">
    /// Whether a hosted handle is reporting 30k traffic. False means the numbers below are
    /// settings for something that is not watching, which is worth saying rather than showing a
    /// timer that will never move.
    /// </param>
    /// <param name="apply">Called with every change, to save it and retune the watcher.</param>
    public TimerSettingsViewModel(
        TimerConfig config, InstanceWatcher watcher, bool fed, Action<TimerConfig> apply)
    {
        _config = config;
        _watcher = watcher;
        _fed = fed;
        _apply = apply;
    }

    /// <summary>
    /// The instance timer as it stands, for the one place it can be read without the game in front.
    /// </summary>
    /// <remarks>
    /// The overlay is where this is actually read, since the game is what you are looking at. This
    /// is here so the thing can be seen working at all - and so a timer that says nothing can be
    /// told apart from a timer that is not running.
    /// </remarks>
    public string Elapsed
    {
        get
        {
            var held = _watcher.Elapsed(DateTimeOffset.UtcNow);

            return _watcher.InInstance
                ? held.TotalHours >= 1
                    ? $"{(int)held.TotalHours}:{held.Minutes:00}:{held.Seconds:00}"
                    : $"{(int)held.TotalMinutes}:{held.Seconds:00}"
                : "--:--";
        }
    }

    /// <summary>What the timer is doing, in a few words.</summary>
    public string State
    {
        get
        {
            var now = DateTimeOffset.UtcNow;

            if (!_fed)
            {
                return "not watching - needs a 30k module hosted";
            }

            if (!_config.Enabled)
            {
                return "turned off";
            }

            if (!_watcher.InInstance)
            {
                return "not in an activity";
            }

            return _watcher.IsDormant(now)
                ? $"quiet for {_watcher.Quiet(now).TotalSeconds:F0}s - "
                    + $"over in {_watcher.UntilEnd(now).TotalSeconds:F0}s unless traffic returns"
                : "in an activity";
        }
    }

    /// <summary>How many activities have been seen and how many have ended.</summary>
    public string Counts =>
        $"{_watcher.Changes} seen  ·  {_watcher.Ends} ended  "
        + $"({_watcher.QuietEnds} by silence)  ·  {_watcher.Suppressed} reconnects ignored";

    /// <summary>Re-reads the running figures. Rides the window's fast tick.</summary>
    public void Tick() => RaiseAll(nameof(Elapsed), nameof(State), nameof(Counts));

    /// <inheritdoc/>
    public override string Name => "Timer";

    /// <inheritdoc/>
    public override string SidebarDetail => IsEnabledSetting ? $"{EndAfterS}s" : "off";

    /// <summary>Whether the instance timer runs at all.</summary>
    public bool IsEnabledSetting
    {
        get => _config.Enabled;
        set => Change(_config with { Enabled = value });
    }

    /// <summary>Seconds of silence before the timer is hidden, while still running.</summary>
    public int HideAfterS
    {
        get => _config.HideAfterS;
        set
        {
            var hide = Math.Clamp(value, 1, 600);

            // Hiding after the end would mean it was never hidden, only stopped - so the end moves
            // out of the way rather than the setting being refused.
            Change(_config with
            {
                HideAfterS = hide,
                EndAfterS = Math.Max(hide, _config.EndAfterS)
            });
        }
    }

    /// <summary>Seconds of silence before the instance is declared over.</summary>
    public int EndAfterS
    {
        get => _config.EndAfterS;
        set
        {
            var end = Math.Clamp(value, 1, 600);

            Change(_config with
            {
                EndAfterS = end,
                HideAfterS = Math.Min(end, _config.HideAfterS)
            });
        }
    }

    /// <summary>What the two numbers mean together, in a sentence.</summary>
    public string Summary =>
        $"Hidden after {HideAfterS}s of silence, still running. Over after {EndAfterS}s. "
        + $"Coming back to the same server inside {EndAfterS}s continues it rather than "
        + "starting again.";

    /// <summary>How many heartbeats the hide wait corresponds to, since that is what it measures.</summary>
    public string HideInBeats => Beats(HideAfterS);

    /// <summary>How many heartbeats the end wait corresponds to.</summary>
    public string EndInBeats => Beats(EndAfterS);

    /// <summary>Nudges the hide wait.</summary>
    public void NudgeHide(int by) => HideAfterS += by;

    /// <summary>Nudges the end wait.</summary>
    public void NudgeEnd(int by) => EndAfterS += by;

    /// <summary>
    /// A wait expressed in heartbeats, which is the evidence it actually rests on.
    /// </summary>
    /// <remarks>
    /// 30k carries a 73-byte heartbeat every 3.02 s (docs/protocol.md, finding 50). Seconds say
    /// how long you wait; beats say how certain you are, and the second is the number that matters
    /// when choosing one.
    /// </remarks>
    private static string Beats(int seconds)
    {
        var beats = (int)Math.Round(seconds / 3.02);
        return beats == 1 ? "1 missed heartbeat" : $"{beats} missed heartbeats";
    }

    private void Change(TimerConfig updated)
    {
        if (updated == _config)
        {
            return;
        }

        _config = updated;
        _apply(updated);

        RaiseAll(
            nameof(IsEnabledSetting), nameof(HideAfterS), nameof(EndAfterS),
            nameof(Summary), nameof(HideInBeats), nameof(EndInBeats), nameof(SidebarDetail));
    }
}
