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
    /// and an error code. The reference implementation gives up at 54 s and this follows it.
    /// </remarks>
    public TimeSpan MaxHold { get; init; } = TimeSpan.FromSeconds(54);

    /// <summary>
    /// After this long, a peer that has stopped retransmitting ends the hold early.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="QuietRetransmit"/>. A peer that has gone quiet has given up on the
    /// data rather than on the connection, so there is nothing left to withhold and continuing only
    /// spends the remaining budget before a reset. 19 s in the reference implementation.
    /// </remarks>
    public TimeSpan QuietHold { get; init; } = TimeSpan.FromSeconds(19);

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
    /// implementation does. Still off by default only because it is untested in game here.
    /// </para>
    /// </remarks>
    public bool Buffering { get; init; }

    /// <summary>Defaults for TCP 7500, taken from the reference implementation.</summary>
    public static ApiBlockOptions Api7500 { get; } = new();
}
