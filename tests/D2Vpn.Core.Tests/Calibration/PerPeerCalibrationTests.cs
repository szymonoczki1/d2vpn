using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// Destiny talks to several peers on 3074 at once. Measured: one capture carried a single peer at
/// 10 uplink packets/second, a later run carried two totalling 23/second. Analysis therefore runs
/// per remote address, as every reference limiter does.
/// </summary>
public class PerPeerCalibrationTests
{
    private static readonly System.Net.IPAddress PeerA = System.Net.IPAddress.Parse("205.209.22.52");
    private static readonly System.Net.IPAddress PeerB = TestPackets.OtherPeer;

    private static CalibrationResult Calibrate(IEnumerable<PacketInfo> packets)
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);
        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    /// <summary>Steady 10 Hz uplink for one peer over a span of seconds.</summary>
    private static void Steady(
        List<PacketInfo> packets, System.Net.IPAddress peer, int from, int to, int size)
    {
        for (var second = from; second < to; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, size + (tick % 4 * 8), second + (tick * 0.1), peer));
            }
        }
    }

    [Fact]
    public void A_hold_on_one_peer_is_not_diluted_by_another_peer_that_keeps_sending()
    {
        // The failure this design prevents. Aggregated, uplink only halves when one of two peers
        // goes quiet - nowhere near the collapse a detector looks for - so the hold vanishes.
        var packets = new List<PacketInfo>();

        Steady(packets, PeerA, 0, 10, 500);
        Steady(packets, PeerB, 0, 30, 600);

        // Peer A is held from ~s10 to ~s20; only handshake packets cross.
        packets.Add(TestPackets.Outbound(3074, 128, 12.4, PeerA));
        packets.Add(TestPackets.Outbound(3074, 136, 14.8, PeerA));
        packets.Add(TestPackets.Outbound(3074, 1166, 17.2, PeerA));
        packets.Add(TestPackets.Outbound(3074, 51, 19.1, PeerA));

        Steady(packets, PeerA, 21, 30, 500);

        var result = Calibrate(packets);

        Assert.Equal(2, result.Peers.Count);
        Assert.True(result.BlockDetected);
        Assert.True(result.HoldWasPartial, "only one of the two peers was held");

        var held = Assert.Single(result.HeldPeers);
        Assert.Equal(PeerA, held.Address);
        Assert.Equal([51, 128, 136, 1166], result.OutboundSignature);
    }

    [Fact]
    public void A_peer_that_never_goes_quiet_contributes_nothing_to_the_signature()
    {
        var packets = new List<PacketInfo>();

        Steady(packets, PeerA, 0, 10, 500);
        Steady(packets, PeerB, 0, 30, 600);
        packets.Add(TestPackets.Outbound(3074, 128, 12.4, PeerA));
        packets.Add(TestPackets.Outbound(3074, 1166, 17.2, PeerA));
        Steady(packets, PeerA, 21, 30, 500);

        var result = Calibrate(packets);

        // 600-624 are peer B's ordinary sizes and must not appear.
        foreach (var size in new[] { 600, 608, 616, 624 })
        {
            Assert.DoesNotContain(size, result.OutboundSignature);
        }
    }

    [Fact]
    public void Both_peers_held_together_is_not_reported_as_partial()
    {
        var packets = new List<PacketInfo>();

        Steady(packets, PeerA, 0, 10, 500);
        Steady(packets, PeerB, 0, 10, 600);

        foreach (var peer in new[] { PeerA, PeerB })
        {
            packets.Add(TestPackets.Outbound(3074, 128, 12.4, peer));
            packets.Add(TestPackets.Outbound(3074, 1166, 17.2, peer));
        }

        Steady(packets, PeerA, 21, 30, 500);
        Steady(packets, PeerB, 21, 30, 600);

        var result = Calibrate(packets);

        Assert.Equal(2, result.HeldPeers.Count);
        Assert.False(result.HoldWasPartial);
    }

    [Fact]
    public void Each_peer_gets_its_own_baseline_rate()
    {
        // Peers tick independently, so one busy peer must not raise another's collapse threshold.
        var packets = new List<PacketInfo>();

        Steady(packets, PeerA, 0, 20, 500);

        for (var second = 0; second < 20; second++)
        {
            for (var tick = 0; tick < 30; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 600, second + (tick / 30.0), PeerB));
            }
        }

        var result = Calibrate(packets);

        var a = result.Peers.Single(p => p.Address.Equals(PeerA));
        var b = result.Peers.Single(p => p.Address.Equals(PeerB));

        Assert.Equal(10, a.BaselineOutboundRate, precision: 0);
        Assert.Equal(30, b.BaselineOutboundRate, precision: 0);
    }
}
