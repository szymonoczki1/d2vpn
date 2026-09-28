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
    /// During a resync window, also pass anything at or below this many payload bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent 27k calibrations produced almost disjoint signatures - 303, 305, 306, 373,
    /// 1185, 1300 the first time and 6, 100, 237, 266, 337, 339, 349, 350, 396, 1300 the second.
    /// Only 1300 appears in both, so the five other sizes shipped from the first run would have
    /// passed nothing at all in the second. An exact-size list does not replicate.
    /// </para>
    /// <para>
    /// The union does have a shape. Every size observed is either under 400 bytes or is one of two
    /// large ones, 1185 and 1300, with nothing in between. The prior art agrees: the stopwatch
    /// <c>KickModule</c> allowlists 9, 11 and 25, notes 100, 284 and 315 in a comment, and passes
    /// 1300. Small control packets carry variable-length fields, so their exact sizes shuffle run
    /// to run while the class stays put - which is what two runs sharing only 1300 looks like.
    /// </para>
    /// <para>
    /// So the rule is a threshold plus the two large sizes named explicitly in
    /// <see cref="ResyncPassLengths"/>. 400 sits just above the largest small size observed (396)
    /// and far below 1185.
    /// </para>
    /// <para>
    /// This leaks more than a list. Run 1 measured 100-byte packets at 5% of normal traffic, so a
    /// threshold passes ordinary game data as well as handshake. What bounds it is the window:
    /// this applies only while a resync is open, never during the block itself.
    /// </para>
    /// <para>
    /// <see langword="null"/> disables it, which is every port but 27k.
    /// </para>
    /// </remarks>
    public int? ResyncPassMaxLength { get; init; }

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
    /// Hold blocked packets and replay them on release, instead of dropping them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing for uplink and optional for downlink, because the two are not symmetric.
    /// The server keeps sending current state, so a dropped inbound packet only delays what the
    /// client learns. Nothing re-sends the player's actions, so a dropped outbound packet is
    /// destroyed - and a shot that is destroyed never happened, where a shot that arrives late
    /// registers against stale context. See <c>docs/protocol.md</c>, finding 36.
    /// </para>
    /// <para>
    /// This alone buffers outbound packets only, even on a hold covering both directions, because
    /// outbound is the only case measured. <see cref="BufferInbound"/> extends it.
    /// </para>
    /// </remarks>
    public bool Buffering { get; init; }

    /// <summary>
    /// Also hold and replay blocked <em>inbound</em> packets, rather than outbound alone.
    /// </summary>
    /// <remarks>
    /// Off even when <see cref="Buffering"/> is on, and deliberately so. Replaying inbound gains
    /// nothing that is known - the server re-sends current state regardless, which is why a
    /// downlink hold works at all while dropping - and delivering a backlog of stale state on
    /// release is the one direction where replay could do harm rather than nothing. Exposed so it
    /// can be measured, not because there is a result behind it.
    /// </remarks>
    public bool BufferInbound { get; init; }

    /// <summary>
    /// Delay blocked packets by <see cref="SlowDelay"/> instead of withholding them until release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A different mode from a hold, not an addition to one. A hold withholds traffic and decides
    /// its fate on release; slow withholds nothing permanently - every packet still arrives, just
    /// late. The peer never sees a gap in the stream, only a stream that is behind.
    /// </para>
    /// <para>
    /// Taken from the reference limiter's own description of its 30k variant, "delays all inbound
    /// packets by a fixed number of seconds", and consistent with what was measured of it: traffic
    /// released in batches, with gaps of 3.91 s and 5.94 s against a 1.32 s baseline
    /// (<c>docs/protocol.md</c>, finding 7). That measurement was of TCP 30k and does not by
    /// itself establish the same mechanism on UDP 3074 - the delay here is configuration for
    /// exactly that reason.
    /// </para>
    /// <para>
    /// Implies buffering, since there is nothing to delay a packet with except a queue. The pass
    /// list still applies and still passes immediately, so a signature size is never delayed and
    /// the machinery that keeps a session alive is untouched.
    /// </para>
    /// </remarks>
    public bool SlowMode { get; init; }

    /// <summary>
    /// How far behind to run the stream when <see cref="SlowMode"/> is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <see cref="SlowThrottle"/> this is a period rather than a depth, so a larger value is
    /// more aggressive, not less: it is the inverse of the send rate.
    /// </para>
    /// <para>
    /// 1 s is measured - both reference limiters use it on 3074, one in its source and the other
    /// on the wire (<c>docs/protocol.md</c>, findings 41 and 42). It is not established for any
    /// other port: finding 7's 3.91 s and 5.94 s gaps are inbound TCP on 30k under a reconnect,
    /// which fixes an order of magnitude and nothing finer.
    /// </para>
    /// </remarks>
    public TimeSpan SlowDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pace the output instead of shifting it: cap the send rate rather than adding a fixed offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The difference between the two slow behaviours, and it is a difference in kind.
    /// </para>
    /// <para>
    /// Off, each packet leaves <see cref="SlowDelay"/> after it arrived. Output rate equals input
    /// rate and the stream keeps its shape - the peer sees a complete, correctly shaped stream that
    /// is late, which is indistinguishable from latency and gets compensated for. A uniform offset
    /// produced one visible in-game effect across a whole session (<c>docs/protocol.md</c>,
    /// finding 40).
    /// </para>
    /// <para>
    /// On, each packet leaves <see cref="SlowDelay"/> after the previous one for that peer, so the
    /// rate is capped whatever arrives. Input outruns output, the backlog grows, and the effective
    /// delay climbs for as long as the hold runs. The peer is not receiving late data, it is
    /// receiving a starved stream - which compounds rather than being absorbed. This is HarryWatch's
    /// behaviour, read from its source, where the delay is 1 s.
    /// </para>
    /// <para>
    /// A throttled delay wants to be much shorter than a fixed one: the desync builds on its own,
    /// so seconds of it are not needed up front.
    /// </para>
    /// <para>
    /// <b>Unlike a fixed offset, this one can drop packets.</b> A fixed delay holds only
    /// rate x delay packets at a time, far inside <see cref="BufferMaxAge"/>, so nothing ever
    /// expires. A throttled backlog grows until its tail reaches that age and then starts expiring
    /// - so the age limit becomes the ceiling on how deep the desync can get, and how long the
    /// hold can run before it turns into a partial block. Raise <see cref="BufferMaxAge"/> to push
    /// that out; the <c>expired</c> count in a run summary says whether it was reached.
    /// </para>
    /// </remarks>
    public bool SlowThrottle { get; init; }

    /// <summary>
    /// Under <see cref="SlowThrottle"/>, packets arriving this close together stay together.
    /// </summary>
    /// <remarks>
    /// Without it a burst is smeared across as many delays as it has packets - four packets 2 ms
    /// apart would leave four seconds apart at a 1 s delay - which throttles far harder than the
    /// rate suggests and destroys any structure a burst carries. Packets arriving inside this
    /// window are spaced by the window instead. 10 ms is HarryWatch's value.
    /// </remarks>
    public TimeSpan SlowBurstWindow { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Most packets to hold per peer before the oldest are discarded.
    /// </summary>
    /// <remarks>
    /// Measured uplink runs about 20 packets/second, so the default is roughly 100 seconds of
    /// holding - far past the ~30 second ceiling a hold survives - at a few megabytes. The cap
    /// exists so a stalled game cannot grow a queue without bound, not because the number is
    /// meaningful.
    /// </remarks>
    public int BufferCapacity { get; init; } = 2048;

    /// <summary>
    /// Discard held packets older than this rather than replaying them.
    /// </summary>
    /// <remarks>
    /// Count alone is the wrong limit. A position update thirty seconds stale is worse than
    /// useless on arrival, and replaying it invites the server to correct something that has long
    /// since moved on. Age bounds what is worth delivering; capacity bounds memory.
    /// </remarks>
    public TimeSpan BufferMaxAge { get; init; } = TimeSpan.FromSeconds(30);

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

        // Taken from what a reference limiter actually permits, rather than from what happened to
        // be flowing. Capturing one of its uplink holds showed exactly 20 packets crossing in 30
        // seconds, in three cycles of the same exchange:
        //
        //   136, 128, 51, 1166, 238, 238
        //   233, 51, 1166, 238
        //   136, 128, 51, 1166, 198, 198, 190, 190, 206, 174
        //
        // An earlier list carried 720, 795, 1254 and 1258 - none of which the reference let out
        // even once. Those were sizes observed crossing our own resync window, which opened
        // fully, so they recorded what was flowing rather than what the exchange needs. They were
        // also the largest, and therefore the likeliest to carry a kill through a hold.
        //
        // 206 and 238 do occur in ordinary traffic, so they are not risk-free; the reference
        // passes them regardless, and the mechanic works with them passed.
        ResyncPassLengths = new HashSet<int> { 51, 174, 190, 198, 206, 233, 238, 1166 },
        ResyncTriggerLengths = new HashSet<int> { 128, 136 },
        ResyncDuration = TimeSpan.FromSeconds(3.5),
        SafetyTimeout = null,

        // Slow on 3074 is a rate cap, not an offset. Both reference limiters do it this way -
        // one in its source, the other measured on the wire at one packet per second - and a
        // fixed offset was tried here first and is indistinguishable from latency in game,
        // which the game compensates for. Findings 40 to 42.
        SlowThrottle = true,
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

        // Deliberately NOT a throttle, unlike 3074. The reference limiter's 30k variant is
        // described by its own README as delaying all inbound packets by a fixed number of
        // seconds, and finding 7 measured it as gaps of 3.91 s and 5.94 s rather than as a rate
        // cap. Different port, different module, different mechanism - and this one is unmeasured
        // here either way.
        SlowThrottle = false,
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

        // Slow on 3074 is a rate cap, not an offset. Both reference limiters do it this way -
        // one in its source, the other measured on the wire at one packet per second - and a
        // fixed offset was tried here first and is indistinguishable from latency in game,
        // which the game compensates for. Findings 40 to 42.
        SlowThrottle = true,
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

        // Slow on 3074 is a rate cap, not an offset. Both reference limiters do it this way -
        // one in its source, the other measured on the wire at one packet per second - and a
        // fixed offset was tried here first and is indistinguishable from latency in game,
        // which the game compensates for. Findings 40 to 42.
        SlowThrottle = true,
        Direction = HoldDirection.Both
    };

    /// <summary>
    /// UDP 27015-27200 uplink. Peer traffic to other players.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calibrated on this machine against a working limiter's 27k module. Six sizes crossed while
    /// uplink was collapsed, five of them far out of proportion to ordinary traffic:
    /// </para>
    /// <code>
    ///   373    2 in hold, never normally      only in hold
    ///   306   15 in hold, 0.1% normally       112x
    ///   303   13 in hold, 0.1% normally        97x
    ///   305   22 in hold, 0.2% normally        82x
    ///  1300   23 in hold, 0.5% normally        34x
    ///  1185    2 in hold, 0.1% normally        15x
    /// </code>
    /// <para>
    /// <b>1300 and 1185 are independent confirmation.</b> The reference implementation opens its
    /// resync window on an outbound 1300 and closes it on an outbound 1176-1194, both read from its
    /// source before this was measured. They fall straight out of the calibration.
    /// </para>
    /// <para>
    /// A second calibration shares only 1300 with the list above, so the six sizes were replaced
    /// by <see cref="HoldModuleOptions.ResyncPassMaxLength"/> plus the two large ones. Nothing passes outside a
    /// resync window either way.
    /// </para>
    /// <para>
    /// Measured on the first in-game run: <b>97.7% of uplink blocked</b> while holding - 39
    /// packets crossed against 1694 dropped in ~40 s - so the threshold costs little here. 27k
    /// uplink is overwhelmingly large packets, and only 26 non-1300 packets fit under the bound
    /// at all. Both directions ran without an error code or a dropped session.
    /// </para>
    /// <para>
    /// No safety timeout, matching 3074. The ~25 s figure recorded elsewhere for 27k belongs to a
    /// kick module targeting another player and says nothing about holding your own traffic - but
    /// 27k is where a BEAVER error is possible, so <c>--timeout-s</c> is worth using until a
    /// ceiling is known.
    /// </para>
    /// </remarks>
    public static HoldModuleOptions Players27k { get; } = new()
    {
        PassLengths = new HashSet<int>(),
        ResyncPassLengths = new HashSet<int> { 1185, 1300 },
        ResyncPassMaxLength = 400,
        ResyncTriggerLengths = new HashSet<int> { 1300 },
        ResyncDuration = TimeSpan.FromSeconds(5),
        SafetyTimeout = null,
        Direction = HoldDirection.Uplink
    };

    /// <summary>
    /// UDP 27015-27200 downlink. Blocks outright, because nothing was found that needs passing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calibrating a downlink hold turned up exactly one size, 9 bytes, 52 of them - and the tool
    /// called it <b>ordinary</b> at 2.8x, because 9-byte packets are 36% of normal inbound traffic.
    /// A size that common is game state as much as handshake, and passing it on length alone would
    /// pass both.
    /// </para>
    /// <para>
    /// So nothing inbound is treated as load-bearing, which is the same answer 3074 downlink gave.
    /// The resync window still opens on an <em>outbound</em> 1300, since it is this machine that
    /// notices a peer has gone quiet whichever direction is held, and during one everything passes
    /// - there being no measured list to narrow it to.
    /// </para>
    /// </remarks>
    public static HoldModuleOptions Players27kDownlink { get; } = new()
    {
        PassLengths = new HashSet<int>(),
        ResyncPassLengths = null,
        ResyncTriggerLengths = new HashSet<int> { 1300 },
        ResyncTriggerDirection = PacketDirection.Outbound,
        ResyncDuration = TimeSpan.FromSeconds(5),
        SafetyTimeout = null,
        Direction = HoldDirection.Downlink
    };

    /// <summary>UDP 27015-27200, both directions at once.</summary>
    /// <remarks>
    /// The uplink pass list, since that is the one that was measured, applied to both directions.
    /// Carries a safety timeout the single-direction holds do not: with nothing flowing either way
    /// there is no traffic left to notice a problem in.
    /// </remarks>
    public static HoldModuleOptions Players27kFull { get; } = new()
    {
        PassLengths = new HashSet<int>(),
        ResyncPassLengths = new HashSet<int> { 1185, 1300 },
        ResyncPassMaxLength = 400,
        ResyncTriggerLengths = new HashSet<int> { 1300 },
        ResyncDuration = TimeSpan.FromSeconds(5),
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

        if (group == DestinyPorts.Players)
        {
            return direction switch
            {
                HoldDirection.Uplink => Players27k,
                HoldDirection.Downlink => Players27kDownlink,
                HoldDirection.Both => Players27kFull,
                _ => null
            };
        }

        return group == DestinyPorts.Instance && direction == HoldDirection.Uplink
            ? Instance30k
            : null;
    }
}
