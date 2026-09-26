using System.Net;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Calibration;

/// <summary>
/// Derives this machine's packet-length signature for a port group by finding, per peer, the
/// stretch where uplink collapsed and reporting what still crossed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Analysis is per peer.</b> Destiny talks to several peers on 3074 at once, each on its own
/// roughly 10 Hz tick, and they join and leave during a session. Measured: one capture carried a
/// single peer at 10 uplink packets/second while a later run carried two peers totalling 23/second.
/// Aggregating them hides exactly what this looks for - a peer going quiet is diluted by peers
/// that did not, and a peer arriving mid-run looks like traffic recovering. Every reference
/// limiter keeps its state per remote address for the same reason.
/// </para>
/// <para>
/// The hold is detected from the traffic, never from a timer or a keypress. An earlier design
/// asked the operator to hold during a fixed interval and differenced the two halves; human
/// reaction time smears the boundary, and payload sizes step in small increments because
/// encrypted payloads pad, so a short baseline misses ordinary sizes and reports them as
/// signature. One real run produced a 105-entry "signature" of ordinary data packets. Uplink
/// collapsing is unambiguous by comparison, and inside a genuine hold only a handful of packets
/// cross at all, so every one is signal.
/// </para>
/// <para>
/// Signatures are not portable between machines or game versions, which is why this exists rather
/// than a table of constants. See <c>docs/protocol.md</c>.
/// </para>
/// </remarks>
public sealed class SignatureCalibrator
{
    private readonly CalibrationOptions _options;
    private readonly Dictionary<string, PeerStream> _peers = [];
    private DateTimeOffset? _firstSeen;

    /// <summary>The group being calibrated.</summary>
    public PortGroup Group { get; }

    /// <summary>Packets recorded so far.</summary>
    public int ObservedCount { get; private set; }

    /// <summary>Distinct peers seen so far.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>Creates a calibrator for <paramref name="group"/>.</summary>
    public SignatureCalibrator(PortGroup group, CalibrationOptions? options = null)
    {
        Group = group.Validate();
        _options = options ?? CalibrationOptions.Default;
    }

    /// <summary>
    /// Records one packet against its peer.
    /// </summary>
    /// <remarks>
    /// Packets whose remote port falls outside <see cref="Group"/> are ignored rather than
    /// rejected, so one stream can feed several calibrators.
    /// </remarks>
    public void Observe(PacketInfo packet)
    {
        if (!Group.Contains(packet.RemotePort))
        {
            return;
        }

        // One clock for the whole run, so every peer's offsets line up on the same timeline.
        _firstSeen ??= packet.Timestamp;

        var offset = Math.Max(0, (packet.Timestamp - _firstSeen.Value).TotalSeconds);
        var key = packet.RemoteAddress.ToString();

        if (!_peers.TryGetValue(key, out var peer))
        {
            peer = _peers[key] = new PeerStream(packet.RemoteAddress);
        }

        (packet.IsOutbound ? peer.Outbound : peer.Inbound).Add(new Observation(offset, packet.PayloadLength));
        ObservedCount++;
    }

    /// <summary>Analyses what has been recorded. Safe to call repeatedly.</summary>
    public CalibrationResult Build()
    {
        var peerResults = new List<PeerCalibration>();

        foreach (var peer in _peers.Values.OrderByDescending(p => p.Outbound.Count + p.Inbound.Count))
        {
            // The held direction is the one expected to collapse; the other is kept for context.
            var held = _options.Direction == PacketDirection.Outbound ? peer.Outbound : peer.Inbound;
            var opposite = _options.Direction == PacketDirection.Outbound ? peer.Inbound : peer.Outbound;

            var analysis = StreamAnalysis.Analyse(held, _options);

            var baselineOutbound = new LengthHistogram();
            var blockOutbound = new LengthHistogram();
            var blockInbound = new LengthHistogram();

            bool InAnyHold(double offset) =>
                analysis.Holds.Any(h => offset > h.Start && offset < h.End);

            foreach (var o in held)
            {
                if (InAnyHold(o.Offset))
                {
                    blockOutbound.Add(o.Length);
                }
                else if (!AfterLastHold(analysis, o.Offset))
                {
                    baselineOutbound.Add(o.Length);
                }

                // Traffic after the final hold is the buffer draining and then recovery. Counting
                // the drain as baseline would repeat in-hold sizes by construction, making every
                // signature look ambiguous. Traffic *between* holds is ordinary: the limiter
                // deliberately lets it through while the game re-establishes, so it belongs in the
                // baseline and is what lets a stray normal packet be told from a real signature.
            }

            foreach (var i in opposite.Where(i => InAnyHold(i.Offset)))
            {
                blockInbound.Add(i.Length);
            }

            var windows = new List<BlockWindow>();
            foreach (var hold in analysis.Holds)
            {
                var duration = hold.End - hold.Start;
                var packets = held.Count(o => o.Offset > hold.Start && o.Offset < hold.End);
                windows.Add(new BlockWindow(
                    TimeSpan.FromSeconds(hold.Start),
                    TimeSpan.FromSeconds(duration),
                    packets,
                    duration > 0 ? packets / duration : 0));
            }

            peerResults.Add(new PeerCalibration(
                peer.Address,
                analysis.BaselineRate,
                windows,
                baselineOutbound,
                blockOutbound,
                blockInbound,
                BuildTimeline(held, opposite, analysis)));
        }

        return new CalibrationResult(Group, peerResults, _options.Direction);
    }

    /// <summary>True when an offset falls after the end of the final detected hold.</summary>
    private static bool AfterLastHold(StreamAnalysis analysis, double offset) =>
        analysis.HoldDetected && offset >= analysis.Holds[^1].End;

    private List<TimelineBucket> BuildTimeline(
        List<Observation> held, List<Observation> opposite, StreamAnalysis analysis)
    {
        var bucketSeconds = _options.BucketSize.TotalSeconds;
        var latest = 0.0;
        foreach (var o in held)
        {
            latest = Math.Max(latest, o.Offset);
        }

        foreach (var i in opposite)
        {
            latest = Math.Max(latest, i.Offset);
        }

        var lastBucket = (int)(latest / bucketSeconds);
        var outCounts = new int[lastBucket + 1];
        var inCounts = new int[lastBucket + 1];

        foreach (var o in held)
        {
            outCounts[Math.Clamp((int)(o.Offset / bucketSeconds), 0, lastBucket)]++;
        }

        foreach (var i in opposite)
        {
            inCounts[Math.Clamp((int)(i.Offset / bucketSeconds), 0, lastBucket)]++;
        }

        var timeline = new List<TimelineBucket>(lastBucket + 1);
        for (var i = 0; i <= lastBucket; i++)
        {
            timeline.Add(new TimelineBucket(
                Second: i,
                Outbound: outCounts[i],
                Inbound: inCounts[i],
                InWindow: analysis.Holds.Any(h => i >= h.StartBucket && i <= h.EndBucket)));
        }

        return timeline;
    }

    private sealed class PeerStream(IPAddress address)
    {
        public IPAddress Address { get; } = address;

        public List<Observation> Outbound { get; } = [];

        public List<Observation> Inbound { get; } = [];
    }
}
