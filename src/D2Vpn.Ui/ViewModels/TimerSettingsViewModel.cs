using D2Vpn.Core.Api;
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
    private readonly Func<BungieActivityWatcher?> _api;
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
    /// <param name="api">
    /// The API corrector as it stands, read fresh each time. A function rather than the object,
    /// because changing a setting replaces it and a captured reference would describe the one
    /// that was torn down.
    /// </param>
    /// <param name="apply">Called with every change, to save it and retune the watcher.</param>
    public TimerSettingsViewModel(
        TimerConfig config,
        InstanceWatcher watcher,
        bool fed,
        Func<BungieActivityWatcher?> api,
        Action<TimerConfig> apply)
    {
        _config = config;
        _watcher = watcher;
        _fed = fed;
        _api = api;
        _apply = apply;
    }

    /// <summary>Ask the Bungie API for the activity's real start time.</summary>
    public bool UseApi
    {
        get => _config.UseApi;
        set => Change(_config with { UseApi = value });
    }

    /// <summary>The account to follow, as Name#1234.</summary>
    public string BungieName
    {
        get => _config.BungieName ?? string.Empty;
        set => Change(_config with
        {
            BungieName = string.IsNullOrWhiteSpace(value) ? null : value.Trim()
        });
    }

    /// <summary>Whether a key has been put in place, without showing it.</summary>
    public string KeyState => BungieKey.Mask(BungieKey.Read());

    /// <summary>Where to put the key.</summary>
    public string KeyPath => BungieKey.Path;

    /// <summary>
    /// What the API half is doing, in a few words.
    /// </summary>
    /// <remarks>
    /// Every way this can fail needs a different thing done about it - a key to paste, a name to
    /// correct, a privacy setting to change - so "something went wrong" would be the one answer
    /// that helps with none of them.
    /// </remarks>
    public string ApiState
    {
        get
        {
            if (!_config.Enabled)
            {
                return "the timer is off";
            }

            if (!_config.UseApi)
            {
                return "off - the timer runs on traffic alone";
            }

            if (BungieKey.Read() is null)
            {
                return $"no key yet - put one in {BungieKey.Path}";
            }

            if (BungieResponses.ParseBungieName(_config.BungieName) is null)
            {
                return "needs your Bungie name, as Name#1234";
            }

            if (_api() is not { } api)
            {
                return "not started - restart the window";
            }

            return api.LastFailure switch
            {
                BungieFailure.Key => "the key was not accepted",
                BungieFailure.NoSuchPlayer => "that name matched no Destiny account",
                BungieFailure.Private =>
                    "your profile does not show activity anonymously - "
                    + "bungie.net, Settings, Privacy",
                BungieFailure.Throttled => "asked too often; it will try again",
                BungieFailure.Unavailable => "could not reach bungie.net",
                _ => api.Player is { } player
                    ? $"following {player.DisplayName}"
                    : "looking up that name"
            };
        }
    }

    /// <summary>Whether the figure on screen is the server's own start or this tool's guess.</summary>
    public string Source => !_watcher.InInstance
        ? string.Empty
        : _watcher.IsCorrected
            ? "from the server"
            : "from traffic";

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

            // The switch first. Turned off is something that was just done on this page, and it
            // makes the hosting question moot - there would be nothing to watch with either way.
            if (!_config.Enabled)
            {
                return "turned off - nothing is being timed";
            }

            if (!_fed)
            {
                return "not watching - needs a 30k module hosted";
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
        + $"({_watcher.QuietEnds} by silence, {_watcher.Superseded} replaced)";

    /// <summary>Re-reads the running figures. Rides the window's fast tick.</summary>
    public void Tick() =>
        RaiseAll(nameof(Elapsed), nameof(State), nameof(Counts), nameof(Source), nameof(ApiState));

    /// <inheritdoc/>
    public override string Name => "Timer";

    /// <inheritdoc/>
    public override string SidebarDetail => IsEnabledSetting ? $"{EndAfterS}s" : "off";

    /// <summary>
    /// Whether the instance timer runs at all.
    /// </summary>
    /// <remarks>
    /// Not whether it is drawn - that is the overlay's own "show the instance timer", and this
    /// switch used to mean the same thing, which left the figure on this page counting away under
    /// a label saying it was off. Off here stops the watching.
    /// </remarks>
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
            nameof(Summary), nameof(HideInBeats), nameof(EndInBeats), nameof(SidebarDetail),
            nameof(UseApi), nameof(BungieName), nameof(KeyState), nameof(ApiState));
    }
}
