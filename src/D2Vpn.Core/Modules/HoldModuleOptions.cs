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
    /// Payload sizes that mean "this peer needs re-establishing".
    /// </summary>
    /// <remarks>
    /// Seeing one starts a resync. Measured with a limiter's own resync disabled, these made up
    /// 90% of everything crossing an uplink hold, repeating at 3-4/s for its entire length - the
    /// game asking over and over because nothing answered.
    /// </remarks>
    public required IReadOnlySet<int> ResyncTriggerLengths { get; init; }

    /// <summary>
    /// Which direction the trigger travels in. Defaults to the direction being held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not necessarily the held direction.</b> Whichever way a hold runs, it is this machine
    /// that notices a peer has gone quiet and sends the reconnect request, so the request is
    /// outbound either way. During a downlink hold the request was seen repeating outbound to
    /// several peers at a time while inbound carried nothing at all.
    /// </para>
    /// <para>
    /// A module therefore has to watch a direction it is not blocking. Passing untouched traffic
    /// without inspecting it would mean never seeing the trigger, and a downlink hold would sit
    /// blocking until its safety timeout with the peer asking the whole time.
    /// </para>
    /// </remarks>
    public PacketDirection? ResyncTriggerDirection { get; init; }

    /// <summary>The direction triggers are watched in, resolving the default.</summary>
    public PacketDirection EffectiveTriggerDirection =>
        ResyncTriggerDirection ?? Direction switch
        {
            HoldDirection.Downlink => PacketDirection.Inbound,
            _ => PacketDirection.Outbound
        };

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
    /// Sizes allowed through during a resync, or <see langword="null"/> to allow everything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resync that stops blocking entirely defeats the hold. Measured: a 3.5 s window every
    /// 8.5 s blocked 53% of downlink - as designed - and a buff picked up during it still arrived
    /// instantly, because a pickup confirmation is a single packet and any open window carries it.
    /// </para>
    /// <para>
    /// Naming the handshake instead keeps the block total for everything else. On 3074 downlink
    /// those sizes are 51 and 233, both measured crossing during resync windows while 233 never
    /// appeared in normal traffic at all. They were briefly shipped as a pass list and then
    /// withdrawn on the grounds that a hold with resync disabled passed nothing - which was the
    /// wrong conclusion. With resync off the exchange never starts, so those packets are never
    /// sent; their absence showed the handshake had not happened, not that it does not need them.
    /// </para>
    /// <para>
    /// Left <see langword="null"/> for uplink, which is validated as it stands and where many
    /// sizes were observed crossing a resync window - 1254, 1258, 720 and 795 among them - so a
    /// short list has not been established there.
    /// </para>
    /// </remarks>
    public IReadOnlySet<int>? ResyncPassLengths { get; init; }

    /// <summary>
    /// How long a peer must stay blocked before another resync may open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this a hold barely blocks anything. The game asks to reconnect every couple of
    /// seconds while it is being held, and opening a window on every request leaves the module
    /// open almost permanently: a measured downlink hold ran six resyncs of 3.5 s inside a
    /// 25 second hold and dropped only 39% of traffic. An object picked up during it registered
    /// instantly, because downlink was flowing for most of the hold.
    /// </para>
    /// <para>
    /// The reference implementation does not behave that way - roughly one resync of ~4 s per
    /// 16 s of holding. Requests arriving during the cooldown are counted but ignored, which
    /// restores blocking stretches long enough to be worth something while still letting the
    /// session re-establish often enough to survive.
    /// </para>
    /// </remarks>
    public TimeSpan ResyncCooldown { get; init; } = TimeSpan.FromSeconds(5);

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

        // Validated at 96% blocked over a 60 second hold, with enemies killed during it
        // reappearing every time - the server never learned of the kills. Replaces a resync that
        // opened the gate entirely and blocked only 70%.
        //
        // The four large sizes are the uncertain part. They are big enough to carry game state,
        // so if a kill ever travels at one of them it would pass. They have not been shown to be
        // necessary, only sufficient: narrowing the list is the open work, and --resync-pass
        // exists to do it.
        ResyncPassLengths = new HashSet<int> { 51, 233, 720, 795, 1254, 1258 },
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

    /// <summary>
    /// UDP 3074 downlink. Blocks outright, because nothing is known to need passing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass list is deliberately empty. Two runs with a reference limiter's resync enabled
    /// showed 51 and 233 crossing during held windows, and an earlier version of this shipped them
    /// as a signature. A third run with that resync <em>disabled</em> contradicted it: downlink
    /// passed <b>nothing at all</b> for nineteen straight seconds on both peers. Packets that were
    /// genuinely required to keep the session alive would have crossed regardless, so 51 and 233
    /// are resync-window traffic rather than a pass list.
    /// </para>
    /// <para>
    /// The resync trigger, however, <em>is</em> known, and is the same 128 as uplink. Whichever
    /// direction is held, it is this machine that notices a peer has gone quiet and asks to
    /// reconnect - so the request is outbound either way. During a downlink hold with resync
    /// disabled, 128-byte packets were seen repeating outbound to several peers at a time while
    /// inbound carried nothing at all. Hence <see cref="HoldModuleOptions.ResyncTriggerDirection"/>: the module has
    /// to watch a direction it is not blocking.
    /// </para>
    /// <para>
    /// Nothing crosses outside a resync window. Inside one, only the handshake does - see
    /// <see cref="HoldModuleOptions.ResyncPassLengths"/> - which keeps the block near-total while
    /// still letting the exchange complete. Validated at 96% blocked over a 60 second hold, twice
    /// the ~30 second ceiling a hold without a working resync dies at.
    ///
    /// What a downlink hold does in play: incoming world state stops arriving, so the client keeps
    /// acting but never learns the result. Interacting with an object does not complete until the
    /// hold is released, and buffs neither expire nor apply while it is on. The held state is not
    /// lost - releasing replays it, which is the release dump showing up as a hundred packets in a
    /// single second.
    /// </para>
    /// </remarks>
    public static HoldModuleOptions Peer3074Downlink { get; } = new()
    {
        PassLengths = new HashSet<int>(),

        // Let the handshake through and nothing else, so the exchange can complete without the
        // hold leaking the state it exists to defer.
        ResyncPassLengths = new HashSet<int> { 51, 233 },
        ResyncTriggerLengths = new HashSet<int> { 128, 136 },
        ResyncTriggerDirection = PacketDirection.Outbound,
        ResyncDuration = TimeSpan.FromSeconds(3.5),

        // Removed: the resync is proven. A 60 second hold - twice the game's ceiling - blocked 96%
        // of downlink, survived without an error code, and deferred a pickup until release. The
        // timeout was there because the resync was unproven, and it was also what prevented
        // proving it: every earlier run ended at 25 s, safely short of the ceiling, so surviving
        // meant nothing.
        SafetyTimeout = null,
        Direction = HoldDirection.Downlink
    };

    /// <summary>
    /// UDP 3074 in both directions at once - the "full" variant of the module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocks everything each way. The pass list is empty because a module carries one list for
    /// whichever direction it holds, and the two directions need different ones - uplink's six
    /// sizes describe what this machine must still be able to send, downlink's describe what the
    /// peer must still be able to send to us. Applying either to both would pass the wrong traffic
    /// in one direction, so a full hold passes nothing outside a resync.
    /// </para>
    /// <para>
    /// Carries a safety timeout: a full block is unproven, and it cuts the reconnect exchange in
    /// both directions at once. Use <c>--timeout-s 0</c> to test past the ~30 second ceiling
    /// deliberately.
    /// </para>
    /// </remarks>
    public static HoldModuleOptions Peer3074Full { get; } = new()
    {
        PassLengths = new HashSet<int>(),

        // Downlink's handshake, as a starting point. Uplink's is not established, so a full hold
        // may need more - override with --resync-pass.
        ResyncPassLengths = new HashSet<int> { 51, 233 },
        ResyncTriggerLengths = new HashSet<int> { 128, 136 },
        ResyncTriggerDirection = PacketDirection.Outbound,
        ResyncDuration = TimeSpan.FromSeconds(3.5),
        SafetyTimeout = TimeSpan.FromSeconds(25),
        Direction = HoldDirection.Both
    };

    /// <summary>
    /// The default options for a group and direction, or <see langword="null"/> if none are known.
    /// </summary>
    public static HoldModuleOptions? For(PortGroup group, HoldDirection direction = HoldDirection.Uplink)
    {
        if (group == DestinyPorts.Peer)
        {
            return direction switch
            {
                HoldDirection.Uplink => Peer3074,
                HoldDirection.Downlink => Peer3074Downlink,
                HoldDirection.Both => Peer3074Full,
                _ => null
            };
        }

        return group == DestinyPorts.Instance && direction == HoldDirection.Uplink
            ? Instance30k
            : null;
    }
}
