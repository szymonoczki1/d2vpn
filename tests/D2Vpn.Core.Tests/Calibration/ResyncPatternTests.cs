using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// A single keypress does not produce a single block. Measured on four peers simultaneously
/// during one 20-second hold: uplink collapsed at s13-16, returned to full rate at s17-20, then
/// collapsed again at s21-28. The middle stretch is the limiter passing traffic while the game
/// re-establishes with its peer - the reference implementation returns "allow" for most packets
/// while a resync is in flight.
/// </summary>
public class ResyncPatternTests
{
    private static CalibrationResult Calibrate(IEnumerable<PacketInfo> packets)
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);
        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    /// <summary>Replays the measured block / resync / block shape at 20 Hz.</summary>
    private static List<PacketInfo> BlockResyncBlock()
    {
        var packets = new List<PacketInfo>();

        void Normal(int from, int to)
        {
            for (var second = from; second < to; second++)
            {
                for (var tick = 0; tick < 20; tick++)
                {
                    packets.Add(TestPackets.Outbound(3074, 500 + (tick % 6 * 8), second + (tick * 0.05)));
                }
            }
        }

        Normal(0, 13);

        // First collapse, s13-16.
        packets.Add(TestPackets.Outbound(3074, 128, 14.2));
        packets.Add(TestPackets.Outbound(3074, 136, 15.6));
        packets.Add(TestPackets.Outbound(3074, 51, 16.4));

        // Resync: full rate returns while the game re-establishes.
        Normal(17, 21);

        // Second collapse, s21-28.
        packets.Add(TestPackets.Outbound(3074, 1166, 22.3));
        packets.Add(TestPackets.Outbound(3074, 233, 24.1));
        packets.Add(TestPackets.Outbound(3074, 1166, 26.5));
        packets.Add(TestPackets.Outbound(3074, 233, 27.8));

        Normal(29, 40);
        return packets;
    }

    [Fact]
    public void Both_collapses_are_found_not_just_the_longest()
    {
        var result = Calibrate(BlockResyncBlock());

        var peer = Assert.Single(result.Peers);
        Assert.Equal(2, peer.Windows.Count);

        Assert.InRange(peer.Windows[0].Start.TotalSeconds, 12.5, 14.5);
        Assert.InRange(peer.Windows[1].Start.TotalSeconds, 20.5, 22.5);
    }

    [Fact]
    public void The_signature_draws_from_every_collapse()
    {
        // Keeping only the longest collapse would lose 128, 136 and 51 entirely.
        var result = Calibrate(BlockResyncBlock());

        Assert.Equal([51, 128, 136, 233, 1166], result.OutboundSignature);
    }

    [Fact]
    public void Traffic_between_collapses_counts_as_normal_not_as_signature()
    {
        // The resync stretch is ordinary traffic the limiter deliberately allowed. Treating it as
        // part of the hold would drag every ordinary size into the signature.
        var result = Calibrate(BlockResyncBlock());

        foreach (var ordinary in new[] { 500, 508, 516, 524, 532, 540 })
        {
            Assert.DoesNotContain(ordinary, result.OutboundSignature);
        }

        Assert.True(result.PooledBaselineOutbound.Total > 300);
    }

    [Fact]
    public void Total_held_time_sums_the_collapses()
    {
        var result = Calibrate(BlockResyncBlock());
        var peer = result.Peers[0];

        // Roughly 4 s plus 8 s, well short of the ~16 s the two spans straddle end to end.
        Assert.InRange(peer.TotalHeld.TotalSeconds, 9, 14);
        Assert.True(peer.CollapseRatio < 0.1);
    }
}
