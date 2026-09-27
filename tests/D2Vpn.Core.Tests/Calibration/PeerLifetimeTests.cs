using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// Peers join and leave mid-session. Offsets are global so every peer shares one timeline, which
/// means a late joiner carries leading zeros and an early leaver carries trailing zeros. Silence
/// is not a hold, and counting it as one is not a harmless mislabel: a measured run reported a
/// peer as "HELD 5-44s, 0% of normal" when its first packet arrived at s44, and the leading zeros
/// dragged its baseline to 2/s against a real rate near 18/s.
/// </summary>
public class PeerLifetimeTests
{
    private static readonly System.Net.IPAddress Established =
        System.Net.IPAddress.Parse("192.0.2.52");

    private static readonly System.Net.IPAddress LateJoiner = TestPackets.OtherPeer;

    private static CalibrationResult Calibrate(IEnumerable<PacketInfo> packets)
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);
        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    private static void Steady(
        List<PacketInfo> packets, System.Net.IPAddress peer, int from, int to)
    {
        for (var second = from; second < to; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500 + (tick % 6 * 8), second + (tick * 0.05), peer));
            }
        }
    }

    [Fact]
    public void A_peer_that_joins_late_is_not_reported_as_held_for_the_time_it_was_absent()
    {
        var packets = new List<PacketInfo>();
        Steady(packets, Established, 0, 60);
        Steady(packets, LateJoiner, 44, 60);

        var result = Calibrate(packets);
        var joiner = result.Peers.Single(p => p.Address.Equals(LateJoiner));

        Assert.False(joiner.HoldDetected);
    }

    [Fact]
    public void A_late_joiners_baseline_ignores_the_silence_before_it_arrived()
    {
        var packets = new List<PacketInfo>();
        Steady(packets, Established, 0, 60);
        Steady(packets, LateJoiner, 44, 60);

        var result = Calibrate(packets);
        var joiner = result.Peers.Single(p => p.Address.Equals(LateJoiner));

        // 20/s while present. Averaging in 44 seconds of absence would report a fraction of that.
        Assert.Equal(20, joiner.BaselineRate, precision: 0);
    }

    [Fact]
    public void A_peer_that_leaves_early_is_not_reported_as_held_afterwards()
    {
        var packets = new List<PacketInfo>();
        Steady(packets, Established, 0, 60);
        Steady(packets, LateJoiner, 0, 15);

        var result = Calibrate(packets);
        var leaver = result.Peers.Single(p => p.Address.Equals(LateJoiner));

        Assert.False(leaver.HoldDetected);
    }

    [Fact]
    public void A_real_hold_on_a_peer_that_joined_late_is_still_found()
    {
        // The fix must not blind us to a genuine hold that happens after a peer arrives.
        var packets = new List<PacketInfo>();
        Steady(packets, Established, 0, 60);
        Steady(packets, LateJoiner, 20, 35);

        packets.Add(TestPackets.Outbound(3074, 128, 36.5, LateJoiner));
        packets.Add(TestPackets.Outbound(3074, 136, 38.2, LateJoiner));
        packets.Add(TestPackets.Outbound(3074, 1166, 41.7, LateJoiner));

        Steady(packets, LateJoiner, 44, 60);

        var result = Calibrate(packets);
        var joiner = result.Peers.Single(p => p.Address.Equals(LateJoiner));

        Assert.True(joiner.HoldDetected);
        Assert.Equal([128, 136, 1166], joiner.Signature);
    }
}
