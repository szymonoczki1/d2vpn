using System.Net;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Calibration;

/// <summary>
/// What a calibration run learned about one port group, peer by peer.
/// </summary>
/// <param name="Group">The group that was observed.</param>
/// <param name="Peers">One entry per remote address seen, busiest first.</param>
/// <param name="HeldDirection">
/// The direction that was expected to collapse. Everything named "held" refers to this stream and
/// "opposite" to the other, so a caller can label its output without guessing.
/// </param>
public sealed record CalibrationResult(
    PortGroup Group,
    IReadOnlyList<PeerCalibration> Peers,
    PacketDirection HeldDirection = PacketDirection.Outbound)
{
    /// <summary>"uplink" or "downlink", for labelling output.</summary>
    public string HeldLabel => HeldDirection == PacketDirection.Outbound ? "uplink" : "downlink";

    /// <summary>The other direction's label.</summary>
    public string OppositeLabel => HeldDirection == PacketDirection.Outbound ? "downlink" : "uplink";

    /// <summary>Peers where a hold was found.</summary>
    public IReadOnlyList<PeerCalibration> HeldPeers => [.. Peers.Where(p => p.HoldDetected)];

    /// <summary>
    /// Held peers that also carried enough normal traffic for the hold to mean anything.
    /// </summary>
    /// <remarks>
    /// A peer ticking over at a packet a second will show "collapses" constantly, because a
    /// one-packet second and a zero-packet second are indistinguishable from a hold at that rate.
    /// One measured run had a peer at 1/s report three separate holds while the peers that
    /// mattered were running at ten times that. Sizes from such a peer are noise, so the signature
    /// is drawn only from peers with a usable baseline.
    /// </remarks>
    public IReadOnlyList<PeerCalibration> ReliablePeers =>
        [.. HeldPeers.Where(p => p.HasUsableBaseline)];

    /// <summary>True when at least one peer showed a collapse.</summary>
    public bool BlockDetected => HeldPeers.Count > 0;

    /// <summary>
    /// True when a hold was found on some peers but not all.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than averaging away. It means either the module holds connections
    /// individually, or a peer joined or left mid-run - and both change what a module has to do,
    /// not merely how the run is measured.
    /// </remarks>
    public bool HoldWasPartial => BlockDetected && HeldPeers.Count < Peers.Count;

    /// <summary>
    /// Every uplink payload size that crossed during a hold, before any filtering.
    /// </summary>
    public IReadOnlyList<int> AllLengthsDuringHold =>
        [.. ReliablePeers.SelectMany(p => p.Signature).Distinct().Order()];

    /// <summary>
    /// The sizes a module should actually pass: those concentrated in the hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not simply everything that crossed. A hold is not perfectly clean - the odd ordinary packet
    /// slips through at a boundary or during a resync - and those strays are indistinguishable
    /// from signature by membership alone. Measured on a four-peer run: 174, 182, 190, 206 and 214
    /// each crossed once during the hold while making up 4-6% of normal traffic, an enrichment
    /// near 1. Meanwhile 233 and 1166 crossed at 112x and 88x their normal frequency.
    /// </para>
    /// <para>
    /// Passing a stray would mean passing ordinary game state, which is the one thing a selective
    /// block must not do, so the filter is applied here rather than left as advice.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> Signature
    {
        get
        {
            var baseline = PooledBaselineHeld;
            var block = PooledBlockHeld;

            return
            [
                .. AllLengthsDuringHold
                    .Where(l => AmbiguousLength.For(l, baseline, block).Enrichment >= MinimumEnrichment)
                    .Order()
            ];
        }
    }

    /// <summary>Enrichment a size must reach to count as signature rather than a stray.</summary>
    public const double MinimumEnrichment = 3.0;

    /// <summary>
    /// Sizes that crossed during the hold but occur about as often in normal traffic.
    /// </summary>
    public IReadOnlyList<AmbiguousLength> ExcludedAsOrdinary
    {
        get
        {
            var baseline = PooledBaselineHeld;
            var block = PooledBlockHeld;

            return
            [
                .. AllLengthsDuringHold
                    .Select(l => AmbiguousLength.For(l, baseline, block))
                    .Where(a => a.Enrichment < MinimumEnrichment)
                    .OrderBy(a => a.PayloadLength)
            ];
        }
    }

    /// <summary>
    /// Every size that crossed during a hold, with its frequency in each phase.
    /// </summary>
    /// <remarks>
    /// One list rather than a kept set and an excluded set, so the boundary between them is
    /// visible. <see cref="AmbiguousLength.Enrichment"/> at or above
    /// <see cref="MinimumEnrichment"/> is what puts a size in <see cref="Signature"/>.
    /// </remarks>
    public IReadOnlyList<AmbiguousLength> AllLengthsWithStats
    {
        get
        {
            var baseline = PooledBaselineHeld;
            var block = PooledBlockHeld;

            return
            [
                .. AllLengthsDuringHold
                    .Select(l => AmbiguousLength.For(l, baseline, block))
                    .OrderByDescending(a => a.Enrichment)
                    .ThenBy(a => a.PayloadLength)
            ];
        }
    }

    /// <summary>Uplink sizes seen before a hold, pooled across held peers.</summary>
    public LengthHistogram PooledBaselineHeld => Pool(p => p.BaselineHeld);

    /// <summary>Uplink sizes seen during a hold, pooled across held peers.</summary>
    public LengthHistogram PooledBlockHeld => Pool(p => p.BlockHeld);

    /// <summary>Downlink sizes seen during a hold, pooled across held peers.</summary>
    public LengthHistogram PooledBlockOpposite => Pool(p => p.BlockOpposite);

    private LengthHistogram Pool(Func<PeerCalibration, LengthHistogram> select)
    {
        var pooled = new LengthHistogram();
        foreach (var peer in ReliablePeers)
        {
            var source = select(peer);
            foreach (var length in source.DistinctLengths)
            {
                for (var i = 0; i < source.CountOf(length); i++)
                {
                    pooled.Add(length);
                }
            }
        }

        return pooled;
    }

    /// <summary>
    /// Signature sizes that also occur in normal traffic, with how often they occur in each.
    /// </summary>
    /// <remarks>
    /// Concentration matters more than membership, so this is a starting point rather than a
    /// verdict. Sizes step in small increments because encrypted payloads pad, so most sizes carry
    /// a count of one or two in any baseline purely by chance. Use <see cref="SeriousConflicts"/>,
    /// which weighs <see cref="AmbiguousLength.Enrichment"/>.
    /// </remarks>
    public IReadOnlyList<AmbiguousLength> AmbiguousLengths
    {
        get
        {
            var baseline = PooledBaselineHeld;
            var block = PooledBlockHeld;

            return
            [
                .. Signature
                    .Where(baseline.Contains)
                    .Select(l => AmbiguousLength.For(l, baseline, block))
            ];
        }
    }

    /// <summary>True when the signature shares no sizes with normal traffic.</summary>
    public bool SignatureIsUnambiguous => BlockDetected && AmbiguousLengths.Count == 0;

    /// <summary>
    /// Signature sizes that are not actually concentrated in the hold.
    /// </summary>
    /// <remarks>
    /// Judged by enrichment - how much more often a size occurs during a hold than normally -
    /// rather than by whether it occurs in normal traffic at all. A size showing up five times in
    /// three hundred baseline packets is indistinguishable from chance when sizes spread across a
    /// hundred-odd values; a size occurring at the same rate in both phases is a genuine collision
    /// and cannot be identified by length alone.
    /// </remarks>
    /// <param name="minimumEnrichment">Ratio below which a size counts as a real conflict.</param>
    public IReadOnlyList<AmbiguousLength> SeriousConflicts(double minimumEnrichment = MinimumEnrichment) =>
        [.. AmbiguousLengths.Where(a => a.Enrichment < minimumEnrichment)];

    /// <summary>True when some peer carried enough normal traffic to judge against.</summary>
    public bool HasUsableBaseline => Peers.Any(p => p.HasUsableBaseline);

    /// <summary>
    /// The span from the earliest hold start to the latest hold end, across held peers.
    /// </summary>
    /// <remarks>
    /// Holds on separate peers rarely line up to the millisecond, so this is the overall reach of
    /// the hold rather than any one peer's window.
    /// </remarks>
    public BlockWindow? CombinedWindow
    {
        get
        {
            if (!BlockDetected)
            {
                return null;
            }

            var start = HeldPeers.Min(p => p.Windows[0].Start);
            var end = HeldPeers.Max(p => p.Windows[^1].Start + p.Windows[^1].Duration);
            var packets = HeldPeers.Sum(p => p.PacketsWhileHeld);
            var duration = end - start;

            return new BlockWindow(
                start,
                duration,
                packets,
                duration.TotalSeconds > 0 ? packets / duration.TotalSeconds : 0);
        }
    }
}

/// <summary>
/// What a calibration run learned about one peer.
/// </summary>
/// <param name="Address">The peer's address.</param>
/// <param name="BaselineRate">Its normal uplink rate in packets per second.</param>
/// <param name="Windows">Its detected holds, in time order. A single keypress can produce several.</param>
/// <param name="BaselineHeld">Uplink sizes seen before the hold.</param>
/// <param name="BlockHeld">Uplink sizes seen during the hold.</param>
/// <param name="BlockOpposite">Downlink sizes seen during the hold.</param>
/// <param name="Timeline">Per-second counts, for diagnosing a run that looks wrong.</param>
public sealed record PeerCalibration(
    IPAddress Address,
    double BaselineRate,
    IReadOnlyList<BlockWindow> Windows,
    LengthHistogram BaselineHeld,
    LengthHistogram BlockHeld,
    LengthHistogram BlockOpposite,
    IReadOnlyList<TimelineBucket> Timeline)
{
    /// <summary>True when this peer went quiet at least once.</summary>
    public bool HoldDetected => Windows.Count > 0;

    /// <summary>Uplink sizes that crossed while this peer was held.</summary>
    public IReadOnlyList<int> Signature =>
        HoldDetected ? BlockHeld.DistinctLengths : [];

    /// <summary>Total time this peer spent held, across all its holds.</summary>
    public TimeSpan TotalHeld =>
        TimeSpan.FromSeconds(Windows.Sum(w => w.Duration.TotalSeconds));

    /// <summary>Uplink packets that crossed while held, across all holds.</summary>
    public int PacketsWhileHeld => Windows.Sum(w => w.OutboundPackets);

    /// <summary>How far this peer's uplink fell, as a fraction of its normal rate.</summary>
    public double CollapseRatio
    {
        get
        {
            var seconds = TotalHeld.TotalSeconds;
            if (seconds <= 0 || BaselineRate <= 0)
            {
                return 1.0;
            }

            return PacketsWhileHeld / seconds / BaselineRate;
        }
    }

    /// <summary>True when this peer carried enough normal traffic to judge against.</summary>
    public bool HasUsableBaseline => BaselineHeld.Total >= 20 && BaselineRate > 0;
}

/// <summary>
/// One counting bucket of a run, used to show what actually happened second by second.
/// </summary>
/// <param name="Second">Offset from the first observed packet.</param>
/// <param name="Outbound">Uplink packets in this bucket.</param>
/// <param name="Inbound">Downlink packets in this bucket.</param>
/// <param name="InWindow">Whether this bucket was counted as part of the hold.</param>
public readonly record struct TimelineBucket(int Second, int Outbound, int Inbound, bool InWindow);

/// <summary>
/// A signature size that also turns up in normal traffic.
/// </summary>
/// <param name="PayloadLength">The size in question.</param>
/// <param name="BaselineCount">Times it occurred before a hold.</param>
/// <param name="BlockCount">Times it occurred during a hold.</param>
/// <param name="BaselineShare">Fraction of all baseline uplink packets that were this size.</param>
/// <param name="BlockShare">Fraction of all in-hold uplink packets that were this size.</param>
/// <param name="Enrichment">
/// <see cref="BlockShare"/> divided by <see cref="BaselineShare"/>. High values mean the size is
/// characteristic of a hold despite also occurring occasionally in normal traffic.
/// </param>
public readonly record struct AmbiguousLength(
    int PayloadLength,
    int BaselineCount,
    int BlockCount,
    double BaselineShare,
    double BlockShare,
    double Enrichment)
{
    /// <summary>Computes the comparison for one payload size.</summary>
    internal static AmbiguousLength For(int length, LengthHistogram baseline, LengthHistogram block)
    {
        var baselineCount = baseline.CountOf(length);
        var blockCount = block.CountOf(length);
        var baselineShare = baseline.Total == 0 ? 0 : (double)baselineCount / baseline.Total;
        var blockShare = block.Total == 0 ? 0 : (double)blockCount / block.Total;
        var enrichment = baselineShare <= 0 ? double.PositiveInfinity : blockShare / baselineShare;

        return new AmbiguousLength(length, baselineCount, blockCount, baselineShare, blockShare, enrichment);
    }
}

/// <summary>
/// A detected stretch of collapsed uplink.
/// </summary>
/// <param name="Start">Offset from the first observed packet of the run.</param>
/// <param name="Duration">How long the collapse lasted.</param>
/// <param name="OutboundPackets">Uplink packets that crossed during it.</param>
/// <param name="OutboundRate">Uplink rate during it, in packets per second.</param>
public sealed record BlockWindow(
    TimeSpan Start,
    TimeSpan Duration,
    int OutboundPackets,
    double OutboundRate);
