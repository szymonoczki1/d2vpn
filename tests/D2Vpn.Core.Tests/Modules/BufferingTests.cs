using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Buffering is what separates deferring an action from destroying it. A reference limiter blocks
/// uplink exactly as completely as this one and then replays what it withheld, which is why it
/// detaches ~6 shots in 9 where dropping manages ~1 in 6.
/// </summary>
public class BufferingTests
{
    private const int Ordinary = 500;

    private static HoldModule Module(bool buffering) =>
        new("PVE", DestinyPorts.Peer,
            MeasuredDefaults.Peer3074 with { Buffering = buffering });

    [Fact]
    public void Without_buffering_a_blocked_packet_is_dropped()
    {
        var module = Module(buffering: false);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void With_buffering_a_blocked_packet_is_held_instead()
    {
        var module = Module(buffering: true);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Buffering_does_not_change_what_passes()
    {
        // It changes the fate of blocked packets only. A signature size still crosses immediately.
        var module = Module(buffering: true);
        module.IsActive = true;

        foreach (var length in MeasuredDefaults.Peer3074.PassLengths)
        {
            var fresh = Module(buffering: true);
            fresh.IsActive = true;
            Assert.Equal(PacketVerdict.Pass, fresh.Decide(TestPackets.Outbound(3074, length, 0)));
        }
    }

    [Fact]
    public void An_idle_module_never_buffers()
    {
        var module = Module(buffering: true);

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void The_unheld_direction_is_never_buffered()
    {
        // An uplink hold must not queue up inbound traffic: the server re-sends state anyway, so
        // holding it achieves nothing and replaying it later is actively wrong.
        var module = Module(buffering: true);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Traffic_outside_the_group_is_never_buffered()
    {
        var module = Module(buffering: true);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(27056, Ordinary, 0)));
    }

    [Fact]
    public void During_a_resync_an_unpassed_packet_is_buffered_rather_than_dropped()
    {
        var module = Module(buffering: true);
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, 128, 1.0));   // opens a window

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 190, 1.5)));
        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.Outbound(3074, Ordinary, 1.6)));
    }

    [Fact]
    public void Buffering_is_off_by_default()
    {
        // It changes what reaches the network, so it is opted into rather than assumed.
        Assert.False(MeasuredDefaults.Peer3074.Buffering);
        Assert.False(MeasuredDefaults.Peer3074Downlink.Buffering);
    }

    [Fact]
    public void Defaults_bound_both_memory_and_staleness()
    {
        var options = MeasuredDefaults.Peer3074;

        // ~20 packets/s measured, so 2048 is about 100 seconds of holding.
        Assert.Equal(2048, options.BufferCapacity);
        Assert.Equal(TimeSpan.FromSeconds(30), options.BufferMaxAge);
    }
}
