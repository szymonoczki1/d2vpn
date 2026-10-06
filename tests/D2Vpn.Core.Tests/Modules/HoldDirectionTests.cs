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

        // 190 is on the resync list only, so it tracks the window exactly. 233 would not work
        // here: it is also on the standing pass list and so crosses either way.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 190, 2.0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 2.1)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 190, 5.0)));
    }

    [Fact]
    public void The_shipped_downlink_hold_resyncs_on_an_outbound_trigger()
    {
        // Against Peer3074Downlink itself, not the uplink defaults with the direction swapped.
        // The test above does the latter, which watches inbound - so the arrangement 3074-dl
        // actually ships with, an outbound trigger opening a window for inbound, had no coverage
        // at all.
        var module = new HoldModule("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);
        module.IsActive = true;

        // Blocked to start with, and the pass list is empty for this one.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 51, 0.1)));

        // The request this machine sends. It is outbound, which a downlink hold does not block,
        // so it passes - and it has to open the window on the way through.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 128, 1.0)));

        // Inside the window the handshake crosses and nothing else does.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 51, 1.1)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 233, 1.2)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.3)));

        // And it closes again after ResyncDuration, 3.5 s.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 51, 5.0)));
    }

    [Fact]
    public void An_inbound_trigger_does_not_open_the_shipped_downlink_window()
    {
        // The direction matters, and this is what the measurement said: during a downlink hold the
        // 128s repeat outbound while inbound carries nothing. An inbound 128 is just a held packet.
        var module = new HoldModule("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 128, 1.0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 51, 1.1)));
    }

    [Fact]
    public void A_reconnect_opens_the_downlink_window_on_its_own_opener()
    {
        // The deadlock this exists for. A reconnect opens with an outbound 425 and the 128 only
        // follows once that 425 has been answered - so with downlink held, blocking the answer
        // meant no 128 was ever sent, the window never opened, and the 425 repeated every second
        // until the hold was lifted by hand. Fifteen in a row were captured.
        var module = new HoldModule("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));

        // The opener. Outbound, so a downlink hold passes it - and it has to open the window on
        // the way through, because nothing later will.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 425, 1.0)));

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 51, 1.1)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.2)));
    }

    [Fact]
    public void The_window_outlasts_the_measured_reconnect_handshake()
    {
        // 2.46s from the 425 to normal traffic, measured twice within 13 ms. The window is 3.5s,
        // and what makes the margin matter is the cooldown behind it: once a window closes there
        // are 5s during which nothing reopens it and nothing crosses.
        var module = new HoldModule("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 425, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 51, 2.46)));
    }

    [Fact]
    public void A_full_hold_passes_the_opener_it_triggers_on()
    {
        // A full hold blocks outbound too, so the opener would otherwise open a window and then be
        // dropped by it - never reaching the peer it was meant to reach.
        var module = new HoldModule("test", DestinyPorts.Peer, MeasuredDefaults.Peer3074Full);
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 425, 1.0)));
    }

    [Fact]
    public void Default_options_hold_uplink()
    {
        Assert.Equal(HoldDirection.Uplink, MeasuredDefaults.Peer3074.Direction);
        Assert.Equal(HoldDirection.Uplink, MeasuredDefaults.Instance30k.Direction);
    }
}
