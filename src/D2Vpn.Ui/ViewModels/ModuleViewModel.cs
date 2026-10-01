using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// One module, as the sidebar and the detail panel see it.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <see cref="IControllable"/> and <see cref="IModuleOptions"/> rather than a
/// copy of their state: toggling here reaches the real module, and the counters are read back from
/// it. Nothing is cached that the host also holds, because two copies of "is it holding" is how a
/// window ends up disagreeing with the thing it is driving.
/// </para>
/// <para>
/// The exception is the keybind, which the host does not own - it comes from the config file and is
/// written back to it.
/// </para>
/// </remarks>
public sealed class ModuleViewModel : ObservableObject
{
    private readonly IControllable _control;
    private int? _bindKey;
    private int? _resyncKey;
    private int? _bufferKey;

    /// <summary>
    /// Raised when the operator changes a setting that belongs in the config file.
    /// </summary>
    /// <remarks>
    /// Only from the setters, never from <see cref="Refresh"/>. Refresh re-raises these properties
    /// four times a second so the window tracks the live module, and saving on that would rewrite
    /// the file continuously for no reason.
    /// </remarks>
    public event Action? SettingChanged;

    /// <summary>Wraps a hosted module.</summary>
    public ModuleViewModel(IControllable control, ModuleConfig? config)
    {
        _control = control;
        Config = config;

        _bindKey = TryParse(config?.Bind);
        _resyncKey = TryParse(config?.ResyncBind);
        _bufferKey = TryParse(config?.BufferBind);
    }

    /// <summary>The config entry this came from, or null for a module with none, such as pause.</summary>
    public ModuleConfig? Config { get; }

    /// <summary>The module's name, which is also how the control surface addresses it.</summary>
    public string Name => _control.Name;

    /// <summary>What shape of thing this is, which decides which panel the window shows.</summary>
    public ModuleKind Kind => _control.Kind;

    /// <summary>
    /// True for anything with an on state, which is everything but a trigger.
    /// </summary>
    /// <remarks>
    /// Pause is one of these. It is a different kind from a traffic hold - it suspends a process
    /// rather than withholding packets - but it is still a thing you switch on and leave on, and it
    /// needs a switch in the window for the same reason every module does.
    /// </remarks>
    public bool IsToggleable => Kind != ModuleKind.Trigger;

    /// <summary>True for a traffic hold or delay line, which is what has switches.</summary>
    public bool IsHold => Kind == ModuleKind.Hold;

    /// <summary>The heading over the on switch, which is not "hold" for a process.</summary>
    public string ToggleSectionLabel => Kind == ModuleKind.Process ? "PAUSE" : "HOLD";

    /// <summary>What the on switch does, in a sentence.</summary>
    public string ToggleDescription => Kind == ModuleKind.Process
        ? "Suspends the game process. Nothing is sent, received, drawn or simulated while it is held."
        : "On until you turn it off. The key below does the same thing.";

    /// <summary>True for a one-shot action such as reconnect.</summary>
    public bool IsTrigger => Kind == ModuleKind.Trigger;

    /// <summary>A short description under the name.</summary>
    public string KindLabel => Kind switch
    {
        ModuleKind.Trigger => "reconnect",
        ModuleKind.Process => "process",
        _ => Name.Contains("-slow", StringComparison.Ordinal) ? "delay line" : "hold"
    };

    /// <summary>
    /// Whether the module participates at all. Turning it off releases it first.
    /// </summary>
    public bool IsEnabled
    {
        get => _control.IsEnabled;
        set
        {
            if (_control.IsEnabled == value)
            {
                return;
            }

            if (!value && _control.IsActive)
            {
                _control.IsActive = false;
            }

            _control.IsEnabled = value;
            RaiseAll(nameof(IsEnabled), nameof(IsActive), nameof(StateLabel), nameof(CanHold));
            SettingChanged?.Invoke();
        }
    }

    /// <summary>True when the hold switch should respond.</summary>
    public bool CanHold => IsEnabled && IsToggleable;

    /// <summary>Whether the module is holding. Setting this reaches the real module.</summary>
    public bool IsActive
    {
        get => _control.IsActive;
        set
        {
            // A disabled module stays off whatever a binding does, since the switch that drives
            // this is disabled rather than hidden and a stale binding could still arrive.
            if (_control.IsActive == value || (!IsEnabled && value))
            {
                return;
            }

            _control.IsActive = value;
            RaiseState();
        }
    }

    /// <summary>True when this module has an autoresync or keepalive to switch.</summary>
    public bool SupportsResync => _control.Options?.SupportsResync == true;

    /// <summary>True when its buffer can be switched.</summary>
    public bool SupportsBuffer => _control.Options?.SupportsBuffer == true;

    /// <summary>True when it has either switch, so the panel knows whether to show the section.</summary>
    public bool HasSwitches => SupportsResync || SupportsBuffer;

    /// <summary>The resync window, or the TCP keepalive.</summary>
    public bool ResyncEnabled
    {
        get => _control.Options?.ResyncEnabled == true;
        set
        {
            if (_control.Options is not { SupportsResync: true } options
                || options.ResyncEnabled == value)
            {
                return;
            }

            options.ResyncEnabled = value;
            Raise();
            SettingChanged?.Invoke();
        }
    }

    /// <summary>Whether withheld packets are kept for replay.</summary>
    public bool BufferEnabled
    {
        get => _control.Options?.BufferEnabled == true;
        set
        {
            if (_control.Options is not { } options || options.BufferEnabled == value)
            {
                return;
            }

            try
            {
                options.BufferEnabled = value;
            }
            catch (InvalidOperationException)
            {
                // A delay line refuses to give up its buffer. The switch is disabled in the window,
                // so this is only reachable by a binding racing a state change - and silently
                // keeping the old value is right either way.
                Raise();
                return;
            }

            Raise();
            SettingChanged?.Invoke();
        }
    }

    /// <summary>True if this module can be reconnected.</summary>
    public bool SupportsReset => _control.SupportsReset;

    /// <summary>The key that toggles it, or null for none.</summary>
    public int? BindKey
    {
        get => _bindKey;
        set
        {
            _bindKey = value;
            RaiseAll(nameof(BindKey), nameof(BindLabel), nameof(HasBind));
        }
    }

    /// <summary>The key that flips resync.</summary>
    public int? ResyncKey
    {
        get => _resyncKey;
        set
        {
            _resyncKey = value;
            RaiseAll(nameof(ResyncKey), nameof(ResyncKeyLabel));
        }
    }

    /// <summary>The key that flips buffering.</summary>
    public int? BufferKey
    {
        get => _bufferKey;
        set
        {
            _bufferKey = value;
            RaiseAll(nameof(BufferKey), nameof(BufferKeyLabel));
        }
    }

    /// <summary>The toggle key as text, or a placeholder.</summary>
    public string BindLabel => Describe(_bindKey);

    /// <summary>The resync key as text.</summary>
    public string ResyncKeyLabel => Describe(_resyncKey);

    /// <summary>The buffer key as text.</summary>
    public string BufferKeyLabel => Describe(_bufferKey);

    /// <summary>What the main key does, which is not "hold" for every kind.</summary>
    public string KeyActionLabel => Kind switch
    {
        ModuleKind.Trigger => "Reconnect",
        ModuleKind.Process => "Pause",
        _ => "Hold"
    };

    /// <summary>True when a key is bound, so the sidebar can show it.</summary>
    public bool HasBind => _bindKey is not null;

    /// <summary>HOLDING, idle or disabled, for the panel.</summary>
    public string StateLabel => !IsEnabled ? "disabled" : IsActive ? "HOLDING" : "idle";

    /// <summary>
    /// The labelled numbers this module reports, which differ by what it actually does.
    /// </summary>
    /// <remarks>
    /// A hold counts packets, a reconnect counts resets, and game pause counts pauses - it handles
    /// no packets at all, so the four packet counters it used to be given were three zeroes and a
    /// pause count labelled "seen".
    /// </remarks>
    public IReadOnlyList<ModuleMetric> Metrics => _control.Metrics;

    /// <summary>The heading over them.</summary>
    public string MetricsLabel => Kind == ModuleKind.Process ? "ACTIVITY" : "TRAFFIC";

    /// <summary>Asks for a reconnect.</summary>
    public void RequestReset() => _control.RequestReset();

    /// <summary>Flips the hold, or fires a trigger.</summary>
    public void Toggle()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (IsTrigger)
        {
            RequestReset();
            return;
        }

        IsActive = !IsActive;
    }

    /// <summary>Re-reads everything the host owns, for the refresh tick.</summary>
    public void Refresh() => RaiseAll(
        nameof(IsActive), nameof(StateLabel), nameof(IsEnabled), nameof(CanHold),
        nameof(ResyncEnabled), nameof(BufferEnabled), nameof(Metrics));

    /// <summary>Raises only the state, for an immediate response to a click.</summary>
    public void RaiseState() => RaiseAll(nameof(IsActive), nameof(StateLabel));

    /// <summary>This module as a config entry, for saving.</summary>
    public ModuleConfig ToConfig() => (Config ?? new ModuleConfig { Name = Name }) with
    {
        Enabled = IsEnabled,
        Bind = _bindKey is { } b ? KeyName.Describe(b) : null,
        ResyncBind = _resyncKey is { } r ? KeyName.Describe(r) : null,
        BufferBind = _bufferKey is { } f ? KeyName.Describe(f) : null,
        Resync = ResyncEnabled,
        Buffer = BufferEnabled
    };

    private static string Describe(int? key) =>
        key is { } code ? KeyName.Describe(code) : "not bound";

    private static int? TryParse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return KeyName.Parse(name);
        }
        catch (Exception)
        {
            // A config with an unrecognised key name should not stop the window opening; it shows
            // as unbound, and rebinding fixes it.
            return null;
        }
    }
}
