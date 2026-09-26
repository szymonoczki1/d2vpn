using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Uplink and downlink holds are separate things needing separate signatures. These pin down the
/// behaviour a boolean got wrong: turning off "inbound always passes" used to block both
/// directions against an uplink pass list, rather than producing a downlink hold.
/// </summary>
public class HoldDirectionTests
{
    private const int Ordinary = 500;
    private const int InPassList = 233;

    private static HoldModule Module(HoldDirection direction) =>
        new("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074 with { Direction = direction });

    [Fact]
    public void An_uplink_hold_leaves_downlink_alone()
    {
        var module = Module(HoldDirection.Uplink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void A_downlink_hold_leaves_uplink_alone()
    {
        var module = Module(HoldDirection.Downlink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void Holding_both_directions_is_reachable_on_purpose()
    {
        // The "full" variant of a module. Previously only obtainable by accident.
        var module = Module(HoldDirection.Both);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void The_pass_list_applies_only_to_the_held_direction()
    {
        // An uplink signature says nothing about which inbound packets matter, so an uplink hold
        // must not be quietly filtering downlink against it.
        var module = Module(HoldDirection.Uplink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, InPassList, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, InPassList, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
    }

    [Fact]
    public void A_downlink_hold_resyncs_on_inbound_triggers()
    {
        var module = Module(HoldDirection.Downlink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 128, 1.0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 2.0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 5.0)));
    }

    [Fact]
    public void Default_options_hold_uplink()
    {
        Assert.Equal(HoldDirection.Uplink, MeasuredDefaults.Peer3074.Direction);
        Assert.Equal(HoldDirection.Uplink, MeasuredDefaults.Instance30k.Direction);
    }
}
