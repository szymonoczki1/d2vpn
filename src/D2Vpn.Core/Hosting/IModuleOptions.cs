using D2Vpn.Core.Modules;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// The options a module exposes for changing while a session is running.
/// </summary>
/// <remarks>
/// <para>
/// Two things are toggleable at runtime, and both mean different things on the two protocols -
/// which is exactly why they need an abstraction rather than a shared flag:
/// </para>
/// <list type="bullet">
/// <item><b>Autoresync.</b> On a UDP hold it is the resync window, disabled by clearing the trigger
/// lengths so no window ever opens. On a TCP hold it is the keepalive, disabled by clearing the
/// pass lengths so the heartbeat stops crossing. Same switch, different field.</item>
/// <item><b>Buffering.</b> Whether withheld packets are kept for replay or destroyed. The same
/// field on both.</item>
/// </list>
/// <para>
/// Turning a switch off has to be reversible, so an implementation keeps the options the module was
/// built with and derives the live ones from them. Deriving rather than mutating is what makes
/// "resync off, then on again" restore the measured trigger set instead of an empty one.
/// </para>
/// </remarks>
public interface IModuleOptions
{
    /// <summary>True if this module has an autoresync or keepalive to turn off.</summary>
    bool SupportsResync { get; }

    /// <summary>True if this module can buffer.</summary>
    bool SupportsBuffer { get; }

    /// <summary>Whether the resync window, or the keepalive, is in force.</summary>
    bool ResyncEnabled { get; set; }

    /// <summary>
    /// Whether withheld packets are kept for replay.
    /// </summary>
    /// <remarks>
    /// Setting this false on a delay-line module throws: a packet cannot be delivered late unless
    /// it was kept, so "slow without a buffer" is not a weaker setting but a broken one.
    /// </remarks>
    bool BufferEnabled { get; set; }

    /// <summary>The switch states as one line of the control protocol.</summary>
    string ToLine();
}

/// <summary>Runtime options for a UDP <see cref="HoldModule"/>.</summary>
public sealed class HoldModuleToggles : IModuleOptions
{
    private readonly HoldModule _module;
    private readonly HoldModuleOptions _baseline;

    private bool _resync;
    private bool _buffer;

    /// <summary>Wraps a module, remembering the options it was built with.</summary>
    public HoldModuleToggles(HoldModule module)
    {
        _module = module;
        _baseline = module.Options;
        _resync = _baseline.ResyncTriggerLengths.Count > 0;
        _buffer = _baseline.Buffering;
    }

    /// <inheritdoc/>
    public bool SupportsResync => _baseline.ResyncTriggerLengths.Count > 0;

    /// <inheritdoc/>
    public bool SupportsBuffer => !_baseline.SlowMode;

    /// <inheritdoc/>
    public bool ResyncEnabled
    {
        get => _resync;
        set
        {
            _resync = value;
            Apply();
        }
    }

    /// <inheritdoc/>
    public bool BufferEnabled
    {
        get => _buffer;
        set
        {
            if (!value && _baseline.SlowMode)
            {
                throw new InvalidOperationException(
                    $"{_module.Name} is a delay line, and a packet cannot be delivered late unless "
                    + "it is kept - so its buffer cannot be turned off");
            }

            _buffer = value;
            Apply();
        }
    }

    /// <inheritdoc/>
    public string ToLine() =>
        $"resync={Describe(SupportsResync, _resync)} buffer={Describe(SupportsBuffer, _buffer)}";

    private void Apply() =>
        _module.Options = _baseline with
        {
            // Clearing the triggers is how a window is prevented from ever opening, and it is what
            // --no-resync does. Restoring means putting the measured set back, which is why the
            // baseline is kept rather than mutated.
            ResyncTriggerLengths = _resync
                ? _baseline.ResyncTriggerLengths
                : new HashSet<int>(),
            Buffering = _buffer
        };

    internal static string Describe(bool supported, bool on) =>
        !supported ? "n/a" : on ? "on" : "off";
}

/// <summary>Runtime options for a TCP <see cref="ApiBlockModule"/>.</summary>
public sealed class ApiBlockToggles : IModuleOptions
{
    private readonly ApiBlockModule _module;
    private readonly ApiBlockOptions _baseline;

    private bool _resync;
    private bool _buffer;

    /// <summary>Wraps a module, remembering the options it was built with.</summary>
    public ApiBlockToggles(ApiBlockModule module)
    {
        _module = module;
        _baseline = module.Options;
        _resync = _baseline.PassLengths.Count > 0;
        _buffer = _baseline.Buffering;
    }

    /// <summary>
    /// True when this module has a keepalive to turn off.
    /// </summary>
    /// <remarks>
    /// 30k does: a 73-byte heartbeat, without which a hold dies in about twenty seconds. 7500 has
    /// no keepalive identified and an empty pass list, so the switch is not applicable there rather
    /// than off.
    /// </remarks>
    public bool SupportsResync => _baseline.PassLengths.Count > 0;

    /// <inheritdoc/>
    public bool SupportsBuffer => _baseline.ReleaseAfter is null;

    /// <inheritdoc/>
    public bool ResyncEnabled
    {
        get => _resync;
        set
        {
            _resync = value;
            Apply();
        }
    }

    /// <inheritdoc/>
    public bool BufferEnabled
    {
        get => _buffer;
        set
        {
            if (!value && _baseline.ReleaseAfter is not null)
            {
                throw new InvalidOperationException(
                    $"{_module.Name} is a delay line, and a packet cannot be delivered late unless "
                    + "it is kept - so its buffer cannot be turned off");
            }

            _buffer = value;
            Apply();
        }
    }

    /// <inheritdoc/>
    public string ToLine() =>
        $"resync={HoldModuleToggles.Describe(SupportsResync, _resync)} "
        + $"buffer={HoldModuleToggles.Describe(SupportsBuffer, _buffer)}";

    private void Apply() =>
        _module.Options = _baseline with
        {
            // The keepalive is a pass list, so clearing it is how the heartbeat is stopped. This is
            // what --no-keepalive does.
            PassLengths = _resync ? _baseline.PassLengths : new HashSet<int>(),
            Buffering = _buffer
        };
}
