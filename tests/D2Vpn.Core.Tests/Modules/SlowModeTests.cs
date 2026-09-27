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
}
