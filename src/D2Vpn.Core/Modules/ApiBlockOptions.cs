using D2Vpn.Core.Interception;

namespace D2Vpn.Core.Modules;

/// <summary>
/// How <see cref="ApiBlockModule"/> behaves.
/// </summary>
/// <remarks>
/// Every number here is read from the reference implementation rather than measured on this
/// machine, and is marked as such where it matters. TCP 7500 has not been calibrated here at all -
/// see <c>docs/protocol.md</c>. Treat these as a starting point with known provenance, not as
/// results.
/// </remarks>
public sealed record ApiBlockOptions
{
    /// <summary>
    /// How long without newly withheld data before the sender is taken to have stalled.
    /// </summary>
    /// <remarks>
    /// The switch from capturing a slice to holding it. While data keeps arriving the sender still
    /// has window left; once it goes quiet for this long it has none, and everything after is
    /// either a retransmission of what is already held or genuinely new traffic that should pass.
    /// 100 ms is the reference implementation's value.
    /// </remarks>
    public TimeSpan CaptureQuiet { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Withhold every inbound data segment for the whole hold, never switching to holding a slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two-phase design assumes a sender that bursts and then stalls: withhold until it goes
    /// quiet for <see cref="CaptureQuiet"/>, then keep that slice and let new data pass. Measured
    /// on this machine, 7500 carries about 2.3 packets a second, so gaps longer than 100 ms are
    /// simply the spacing between updates - the capture fires after the first segment and
    /// everything later passes. Seven consecutive holds each withheld exactly one segment.
    /// </para>
    /// <para>
    /// With this set, nothing is ever "captured" and every inbound data segment is withheld for as
    /// long as the hold runs. Stronger, and correspondingly shorter-lived: the receive window
    /// closes, the sender exhausts its retries, and the reset arrives sooner. The ceiling matters
    /// more here, not less.
    /// </para>
    /// <para>
    /// <b>On by default.</b> The two-phase behaviour is what the reference implementation does and
    /// remains available, but it does not fit this channel and holding a single segment is not
    /// what anyone asking for a hold means.
    /// </para>
    /// </remarks>
    public bool HoldEverything { get; init; } = true;

    /// <summary>
    /// How many bytes the withheld slice must cover before the hold is considered established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1460 is one maximum segment on a standard 1500-byte path, and it is the reference
    /// implementation's constant. The reasoning behind it is not stated there and is not verified
    /// here: withholding a segment's worth of unacknowledged data is roughly where a sender's
    /// window closes and it stops producing anything new, but the exact figure is inherited rather
    /// than derived.
    /// </para>
    /// <para>
    /// What it is actually used for is starting the clock. Nothing is withheld differently before
    /// or after; it only marks the point from which <see cref="MaxHold"/> and
    /// <see cref="QuietHold"/> are measured.
    /// </para>
    /// </remarks>
    public int CaptureBytes { get; init; } = 1460;

    /// <summary>
    /// The longest the hold may run after the slice is established.
    /// </summary>
    /// <remarks>
    /// Unlike a 3074 uplink hold, which has no known ceiling, this one ends in a connection reset
    /// and an error code. Measured on this machine, an 86.5 s hold was reset and a 75.2 s hold
    /// released cleanly, so the ceiling sits somewhere between - well past the reference
    /// implementation's 54 s (<c>docs/protocol.md</c>, finding 44).
    ///
    /// <b>Zero by default, meaning no limit.</b> The effect lasts as long as the hold does: the
    /// game is deprived of API data for every second it runs, and a shorter hold is simply a
    /// smaller effect. So how long to hold is the operator's call, not a number this type should
    /// pick, and the only automatic exit is a reset - which is the connection already being gone.
    ///
    /// Set it to switch on error-code prevention, at whatever length is wanted.
    /// </remarks>
    public TimeSpan MaxHold { get; init; }

    /// <summary>
    /// After this long, a peer that has stopped retransmitting ends the hold early.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="QuietRetransmit"/>. A peer that has gone quiet has given up on the
    /// data rather than on the connection, so there is nothing left to withhold and continuing only
    /// spends the remaining budget before a reset. 19 s in the reference implementation.
    ///
    /// <b>Zero by default, meaning off</b>, because that reasoning does not survive measurement. A
    /// peer here stops retransmitting about 25 s in, and what stops then is the counters moving -
    /// not the effect. The game stays deprived for as long as the hold runs, so a quiet peer is
    /// not a reason to end it.
    /// </remarks>
    public TimeSpan QuietHold { get; init; }

    /// <summary>
    /// How long without a suppressed retransmission counts as the peer having given up.
    /// </summary>
    /// <remarks>24 s in the reference implementation.</remarks>
    public TimeSpan QuietRetransmit { get; init; } = TimeSpan.FromSeconds(24);

    /// <summary>
    /// Hold withheld segments for replay instead of dropping them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and for a different reason than on 3074. There a withheld packet is the only
    /// copy in existence, so buffering is the difference between an action arriving late and never
    /// having happened. Here the sender keeps its own copy and retransmits it, so nothing is lost
    /// by dropping.
    /// </para>
    /// <para>
    /// What it does buy is the wait after unticking. A withheld segment that was destroyed arrives
    /// only when the peer next retransmits it, and every suppression doubles that interval - so a
    /// few seconds of holding backs the peer off to around a second, which is felt as a delay
    /// before anything changes. Replaying delivers it at once instead.
    /// </para>
    /// <para>
    /// A replayed TCP segment cannot go out exactly as captured: its acknowledgement number is
    /// stale by then, and delivering it is a duplicate acknowledgement. <see cref="TcpAckRewriter"/>
    /// brings that field forward and recalculates the checksum, which is what the reference
    /// implementation does.
    /// </para>
    /// <para>
    /// <b>On by default</b>, having been measured: a 61 s hold replayed all three withheld
    /// segments, every one of which needed its acknowledgement brought forward, and recovery after
    /// unticking was visibly faster than waiting for the sender to re-send
    /// (<c>docs/protocol.md</c>, finding 45).
    /// </para>
    /// </remarks>
    public bool Buffering { get; init; } = true;

    /// <summary>
    /// Defaults for TCP 7500: hold until the peer resets, and nothing else.
    /// </summary>
    public static ApiBlockOptions Api7500 { get; } = new();

    /// <summary>
    /// The reference implementation's ceilings, for comparison and for anyone who wants them.
    /// </summary>
    /// <remarks>
    /// None of these were measured here, and the one that can be checked - 54 s - is about thirty
    /// seconds short of where a reset was actually observed.
    /// </remarks>
    public static ApiBlockOptions Reference { get; } = new()
    {
        MaxHold = TimeSpan.FromSeconds(54),
        QuietHold = TimeSpan.FromSeconds(19),
        QuietRetransmit = TimeSpan.FromSeconds(24),
        HoldEverything = false,
        Buffering = false
    };
}
