using D2Vpn.Core.Network;

namespace D2Vpn.Core.Modules;

/// <summary>
/// Settings for a <see cref="HoldModule"/>.
/// </summary>
/// <remarks>
/// Every value here is configuration with a measured default, never a constant. Packet sizes drift
/// with game version, loadout, ornament and catalyst, which is why the calibrator exists - a value
/// correct on one machine is wrong on another, and it fails silently. See <c>docs/protocol.md</c>.
/// </remarks>
public sealed record HoldModuleOptions
{
    /// <summary>
    /// Uplink payload sizes that must always be allowed through while holding.
    /// </summary>
    /// <remarks>
    /// Blocking these too kills the session: they are the messages the game emits once its peer
    /// stops acknowledging, and passing them is the whole technique. Derive a machine's own set
    /// with <c>d2vpn calibrate</c>.
    /// </remarks>
    public required IReadOnlySet<int> PassLengths { get; init; }

    /// <summary>
    /// Uplink payload sizes that mean "the peer is asking to reconnect".
    /// </summary>
    /// <remarks>
    /// Seeing one starts a resync. Measured with a limiter's own resync disabled, these made up
    /// 90% of everything crossing a hold, repeating at 3-4/s for its entire length - the game
    /// asking over and over because nothing answered.
    /// </remarks>
    public required IReadOnlySet<int> ResyncTriggerLengths { get; init; }

    /// <summary>
    /// How long to stop blocking once a reconnect request is seen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A timed resync is deliberately the first implementation. Measured: real resyncs ran about
    /// 3-4 seconds of full-rate uplink before blocking resumed.
    /// </para>
    /// <para>
    /// The reference implementation does better, reading a counter out of the payload to detect
    /// exactly when an exchange completes. That is more accurate and also the part most worth
    /// deriving from our own captures rather than copying, so it is deferred - and where a timer
    /// is wrong, it will be wrong visibly.
    /// </para>
    /// </remarks>
    public TimeSpan ResyncDuration { get; init; } = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// Force the module off after this long, or <see langword="null"/> for no limit.
    /// </summary>
    /// <remarks>
    /// Ceilings belong to individual modules, never to the app. A 3074 uplink hold has run over 20
    /// minutes with the reconnect exchange being completed; a held 30k reconnect errors out around
    /// 25-30 seconds because nothing protects it. Neither number generalises to the other.
    /// </remarks>
    public TimeSpan? SafetyTimeout { get; init; }

    /// <summary>
    /// Which direction this module holds. Traffic in any other direction passes untouched.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="HoldDirection.Uplink"/>, which is what the AHK macros drive and the
    /// only direction measured so far. Downlink held a steady rate throughout every uplink hold
    /// captured, and that is what keeps a block directional rather than a dead connection.
    /// </remarks>
    public HoldDirection Direction { get; init; } = HoldDirection.Uplink;
}

/// <summary>
/// Measured starting points for each port group.
/// </summary>
/// <remarks>
/// These are defaults to be overridden by calibration, not truths. They came off one machine, and
/// two of the five constants a reference limiter hardcodes did <em>not</em> match it. Shipping
/// them unchanged for a different connection is exactly the mistake this project exists to avoid.
/// </remarks>
public static class MeasuredDefaults
{
    /// <summary>
    /// UDP 3074, reproduced across two independent runs.
    /// </summary>
    /// <remarks>
    /// 128, 136 and 233 also appear verbatim in a reference implementation written years earlier
    /// against a different connection, so that much of the handshake looks stable. 1166 is the
    /// large data packet that completes an exchange, and it is the one that varies - the same
    /// reference hardcodes 1258.
    /// </remarks>
    public static HoldModuleOptions Peer3074 { get; } = new()
    {
        PassLengths = new HashSet<int> { 36, 51, 128, 136, 233, 1166 },
        ResyncTriggerLengths = new HashSet<int> { 128, 136 },
        ResyncDuration = TimeSpan.FromSeconds(3.5),
        SafetyTimeout = null,
        Direction = HoldDirection.Uplink
    };

    /// <summary>
    /// TCP 30000-30009. Unmeasured, and carrying a safety timeout because of it.
    /// </summary>
    /// <remarks>
    /// No signature has been derived for this group yet, so the pass list is empty and a hold here
    /// blocks outright. A held 30k reconnect produces an error code at roughly 25-30 seconds, and
    /// nothing equivalent to the 3074 handshake is known to protect it, so the module disables
    /// itself first. Calibrate before trusting this.
    /// </remarks>
    public static HoldModuleOptions Instance30k { get; } = new()
    {
        PassLengths = new HashSet<int>(),
        ResyncTriggerLengths = new HashSet<int>(),
        ResyncDuration = TimeSpan.FromSeconds(3.5),
        SafetyTimeout = TimeSpan.FromSeconds(20),
        Direction = HoldDirection.Uplink
    };

    /// <summary>The default options for a group, or <see langword="null"/> if none are known.</summary>
    public static HoldModuleOptions? For(PortGroup group)
    {
        if (group == DestinyPorts.Peer)
        {
            return Peer3074;
        }

        return group == DestinyPorts.Instance ? Instance30k : null;
    }
}
