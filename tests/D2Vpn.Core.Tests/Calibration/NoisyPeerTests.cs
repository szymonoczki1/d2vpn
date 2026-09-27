using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// A peer ticking over at a packet a second reports "collapses" constantly, because at that rate
/// a quiet second is indistinguishable from a hold. One measured run had such a peer report three
/// separate holds while the peers that mattered ran at ten times the rate.
/// </summary>
public class NoisyPeerTests
{
    private static readonly System.Net.IPAddress Busy =
        System.Net.IPAddress.Parse("192.0.2.52");

    private static readonly System.Net.IPAddress Trickle = TestPackets.OtherPeer;

    private static CalibrationResult Calibrate(IEnumerable<PacketInfo> packets)
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);
        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    private static List<PacketInfo> TwoPeersOneOfThemBarelyThere()
    {
        var packets = new List<PacketInfo>();

        // A busy peer with a clear hold from s12 to s22.
        for (var second = 0; second < 30; second++)
        {
            if (second is >= 12 and < 22)
            {
                continue;
            }

            for (var tick = 0; tick < 20; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500 + (tick % 4 * 8), second + (tick * 0.05), Busy));
            }
        }

        packets.Add(TestPackets.Outbound(3074, 128, 14.3, Busy));
        packets.Add(TestPackets.Outbound(3074, 1166, 18.7, Busy));

        // A peer sending roughly one packet a second, with gaps that mean nothing.
        foreach (var at in new[] { 0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 11.5, 12.5, 20.5, 26.5, 27.5, 28.5 })
        {
            packets.Add(TestPackets.Outbound(3074, 777, at, Trickle));
        }

        return packets;
    }

    [Fact]
    public void A_trickle_peer_does_not_contribute_sizes_to_the_signature()
    {
        var result = Calibrate(TwoPeersOneOfThemBarelyThere());

        Assert.DoesNotContain(777, result.Signature);
        Assert.Equal([128, 1166], result.Signature);
    }

    [Fact]
    public void A_trickle_peer_is_still_listed_so_it_can_be_seen()
    {
        // Excluded from the signature, not hidden: the operator should be able to tell the
        // difference between a peer that was ignored and one that was never there.
        var result = Calibrate(TwoPeersOneOfThemBarelyThere());

        Assert.Equal(2, result.Peers.Count);
        Assert.Contains(result.Peers, p => p.Address.Equals(Trickle));
        Assert.DoesNotContain(result.ReliablePeers, p => p.Address.Equals(Trickle));
    }

    [Fact]
    public void A_busy_peer_with_a_real_hold_is_reliable()
    {
        var result = Calibrate(TwoPeersOneOfThemBarelyThere());
        var busy = result.ReliablePeers.Single();

        Assert.Equal(Busy, busy.Address);
        Assert.True(busy.HasUsableBaseline);
    }
}
