using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// Releasing a hold drains the buffer as one instantaneous dump - measured at 99 packets in a
/// second against a 10/s baseline - and traffic takes a moment to settle afterwards. That settling
/// looks like a shallow collapse, and in a real run it produced two phantom holds whose sizes then
/// polluted the signature.
/// </summary>
public class ReleaseBurstTests
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

    /// <summary>Normal, a real hold, a 99-packet release dump, then a slow settle.</summary>
    private static List<PacketInfo> HoldThenRelease()
    {
        var packets = new List<PacketInfo>();

        void Normal(int from, int to)
        {
            for (var second = from; second < to; second++)
            {
                for (var tick = 0; tick < 10; tick++)
                {
                    packets.Add(TestPackets.Outbound(3074, 500 + (tick % 4 * 8), second + (tick * 0.1)));
                }
            }
        }

        Normal(0, 11);

        // The real hold: nothing crosses at all.
        // s11-29 is silent.

        // The release dump, all inside one second.
        for (var i = 0; i < 99; i++)
        {
            packets.Add(TestPackets.Outbound(3074, 700 + (i % 7 * 8), 30.0 + (i * 0.001)));
        }

        // Settling: a near-empty second, a partial one, then normal.
        packets.Add(TestPackets.Outbound(3074, 333, 32.4));
        for (var i = 0; i < 5; i++)
        {
            packets.Add(TestPackets.Outbound(3074, 444, 32.5 + (i * 0.05)));
        }

        Normal(33, 50);
        return packets;
    }

    [Fact]
    public void The_real_hold_is_found()
    {
        var result = Calibrate(HoldThenRelease());
        var peer = result.Peers.Single();

        Assert.True(peer.HoldDetected);
        Assert.InRange(peer.Windows[0].Start.TotalSeconds, 10.5, 11.5);
    }

    [Fact]
    public void Settling_after_the_release_is_not_reported_as_a_second_hold()
    {
        var result = Calibrate(HoldThenRelease());
        var peer = result.Peers.Single();

        Assert.Single(peer.Windows);
    }

    [Fact]
    public void Sizes_from_the_settling_period_stay_out_of_the_signature()
    {
        // This is what the bug actually cost: phantom windows contributed packet sizes that a
        // module would then have been told to pass.
        var result = Calibrate(HoldThenRelease());

        Assert.DoesNotContain(333, result.Signature);
        Assert.DoesNotContain(444, result.Signature);
    }

    [Fact]
    public void A_fully_blocked_hold_yields_an_empty_signature_rather_than_an_invented_one()
    {
        // With nothing crossing, there is nothing to pass. Reporting sizes here would be fabricating
        // a pass list out of unrelated traffic.
        var result = Calibrate(HoldThenRelease());

        Assert.True(result.BlockDetected);
        Assert.Empty(result.Signature);
    }
}
