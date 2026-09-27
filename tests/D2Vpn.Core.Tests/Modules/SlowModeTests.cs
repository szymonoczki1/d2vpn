using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Slow is a different mode from a hold, not a stronger one. A hold withholds traffic and decides
/// its fate on release; slow withholds nothing permanently - every packet arrives, just late, so
/// the peer sees a stream that is behind rather than a gap in one.
/// </summary>
public class SlowModeTests
{
    private const int Ordinary = 500;

    private static HoldModule Module(HoldModuleOptions options) =>
        new("PVE", DestinyPorts.Peer, options);

    [Fact]
    public void Slow_buffers_rather_than_dropping()
    {
        var module = Module(MeasuredDefaults.Peer3074 with { SlowMode = true });
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Slow_delays_inbound_too()
    {
        // Unlike a plain hold, where buffering inbound gains nothing because the server re-sends
        // state anyway, delaying inbound is the whole of what the reference limiter's 30k variant
        // does - so slow covers both directions without being asked.
        var module = Module(MeasuredDefaults.Peer3074Downlink with { SlowMode = true });
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Slow_does_not_need_buffering_switched_on_separately()
    {
        // A packet cannot be delivered late unless it was kept, so slow implies the queue.
        var module = Module(MeasuredDefaults.Peer3074 with { SlowMode = true, Buffering = false });
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void The_pass_list_still_passes_immediately_under_slow()
    {
        // Delaying a handshake size would put the machinery that keeps a session alive behind the
        // same delay as everything else, which is how a hold turns into an error code.
        var options = MeasuredDefaults.Peer3074 with { SlowMode = true };
        var passing = options.PassLengths.First();
        var module = Module(options);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, passing, 0)));
    }

    [Fact]
    public void An_idle_slow_module_touches_nothing()
    {
        var module = Module(MeasuredDefaults.Peer3074 with { SlowMode = true });

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Slow_is_off_by_default()
    {
        Assert.False(MeasuredDefaults.Peer3074.SlowMode);
        Assert.False(MeasuredDefaults.Peer3074Downlink.SlowMode);
        Assert.False(MeasuredDefaults.Peer3074Full.SlowMode);
    }

    [Fact]
    public void The_default_delay_is_shorter_than_the_default_buffer_age()
    {
        // Otherwise every delayed packet would expire before it came due, and slow would be an
        // elaborate way of dropping everything.
        var options = MeasuredDefaults.Peer3074;
        Assert.True(options.SlowDelay < options.BufferMaxAge);
    }

    [Fact]
    public void An_uplink_slow_hold_leaves_downlink_alone()
    {
        // Slow delays whichever direction the hold covers, not both. The direction gate runs
        // before the slow logic, so an uplink hold never sees an inbound packet - which is what
        // makes "3074 ul slow" delay only what this machine sends.
        var module = Module(MeasuredDefaults.Peer3074 with { SlowMode = true });
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, 500, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 500, 0)));
    }

    [Fact]
    public void A_full_slow_hold_delays_both_directions()
    {
        var module = Module(MeasuredDefaults.Peer3074Full with { SlowMode = true });
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, 500, 0)));
        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Inbound(3074, 500, 0)));
    }

    [Fact]
    public void Slow_on_3074_is_a_rate_cap_rather_than_an_offset()
    {
        // Measured on the wire in both reference limiters and here: one packet per second, not a
        // stream shifted by a second. A fixed offset reads as latency, which the game compensates
        // for. Findings 40 to 42.
        Assert.True(MeasuredDefaults.Peer3074.SlowThrottle);
        Assert.True(MeasuredDefaults.Peer3074Downlink.SlowThrottle);
        Assert.True(MeasuredDefaults.Peer3074Full.SlowThrottle);
    }

    [Fact]
    public void The_measured_slow_period_is_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), MeasuredDefaults.Peer3074.SlowDelay);
    }

    [Fact]
    public void Slow_on_30k_stays_a_fixed_offset()
    {
        // A different module in the prior art, not a setting of the same one: its README calls it
        // a fixed delay on inbound, and finding 7 measured gaps rather than a rate cap.
        Assert.False(MeasuredDefaults.Instance30k.SlowThrottle);
    }
}
