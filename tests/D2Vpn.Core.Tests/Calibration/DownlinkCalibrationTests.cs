using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// A downlink hold collapses inbound while outbound keeps flowing - the mirror of an uplink hold.
/// Watching the wrong stream finds nothing, because the traffic still flowing looks entirely
/// normal, so the calibrator has to be told which direction is being held.
/// </summary>
public class DownlinkCalibrationTests
{
    private static CalibrationResult Calibrate(
        IEnumerable<PacketInfo> packets, PacketDirection direction)
    {
        var calibrator = new SignatureCalibrator(
            DestinyPorts.Peer, new CalibrationOptions { Direction = direction });

        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    /// <summary>Inbound collapses from s12 to s22; outbound runs at full rate throughout.</summary>
    private static List<PacketInfo> DownlinkHold()
    {
        var packets = new List<PacketInfo>();

        for (var second = 0; second < 30; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                var at = second + (tick * 0.05);

                // Uplink is untouched for the whole run.
                packets.Add(TestPackets.Outbound(3074, 500 + (tick % 6 * 8), at));

                if (second < 12 || second >= 22)
                {
                    packets.Add(TestPackets.Inbound(3074, 600 + (tick % 6 * 8), at));
                }
            }
        }

        // The handful of inbound packets that survive the hold.
        packets.Add(TestPackets.Inbound(3074, 44, 13.4));
        packets.Add(TestPackets.Inbound(3074, 91, 15.8));
        packets.Add(TestPackets.Inbound(3074, 44, 18.2));
        packets.Add(TestPackets.Inbound(3074, 1258, 20.6));
        return packets;
    }

    [Fact]
    public void A_downlink_hold_is_found_when_the_calibrator_is_told_to_look_inbound()
    {
        var result = Calibrate(DownlinkHold(), PacketDirection.Inbound);

        Assert.True(result.BlockDetected);
        Assert.Equal([44, 91, 1258], result.Signature);
    }

    [Fact]
    public void The_same_capture_looks_like_nothing_happened_if_the_wrong_stream_is_watched()
    {
        // This is the failure mode the option prevents: uplink never collapsed, so an
        // outbound-only calibrator reports a perfectly ordinary session.
        var result = Calibrate(DownlinkHold(), PacketDirection.Outbound);

        Assert.False(result.BlockDetected);
        Assert.Empty(result.Signature);
    }

    [Fact]
    public void The_opposite_direction_is_kept_as_context_not_as_signature()
    {
        var result = Calibrate(DownlinkHold(), PacketDirection.Inbound);

        // Uplink kept flowing through the hold, and none of its sizes are signature.
        Assert.True(result.PooledBlockOpposite.Total > 100);
        foreach (var ordinary in new[] { 500, 508, 516, 524 })
        {
            Assert.DoesNotContain(ordinary, result.Signature);
        }
    }

    [Fact]
    public void Uplink_remains_the_default_so_existing_behaviour_is_unchanged()
    {
        Assert.Equal(PacketDirection.Outbound, CalibrationOptions.Default.Direction);
    }
}
