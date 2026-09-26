using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Whichever direction a hold runs, it is this machine that notices a peer has gone quiet and
/// sends the reconnect request - so the request is outbound either way. A downlink module must
/// therefore watch traffic it is not blocking, or it never resyncs at all.
/// </summary>
public class CrossDirectionTriggerTests
{
    private const int Ordinary = 600;
    private const int ReconnectRequest = 128;
    private const int HandshakeReply = 233;

    private static HoldModule Downlink() =>
        new("3074 DL", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);

    [Fact]
    public void An_outbound_trigger_starts_a_resync_during_a_downlink_hold()
    {
        var module = Downlink();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));

        // The request goes out in traffic the module is not blocking.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0)));
        Assert.Equal(HoldState.Resyncing, module.PeerStates.Values.Single());

        // The handshake reply crosses so the exchange can complete...
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, HandshakeReply, 1.5)));

        // ...but game state does not. A resync that opens the gate entirely defeats the hold,
        // because one packet is enough to carry a pickup through.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.6)));
    }

    [Fact]
    public void Blocking_resumes_after_the_resync_window()
    {
        var module = Downlink();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));

        // The handshake crosses while the window is open, and stops once it closes.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, HandshakeReply, 4.0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, HandshakeReply, 4.6)));
    }

    [Fact]
    public void The_trigger_itself_is_never_blocked_when_it_travels_the_other_way()
    {
        // It is not in the pass list, and does not need to be: the module does not block outbound.
        Assert.DoesNotContain(
            ReconnectRequest, MeasuredDefaults.Peer3074Downlink.PassLengths);

        var module = Downlink();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 0)));
    }

    [Fact]
    public void Triggers_are_tracked_per_peer()
    {
        // One peer reconnecting must not lift the hold on another.
        var module = Downlink();
        module.IsActive = true;
        var other = TestPackets.OtherPeer;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));

        // The resyncing peer may complete its handshake; the other peer stays fully blocked.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, HandshakeReply, 1.5)));
        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Inbound(3074, HandshakeReply, 1.5, other)));
    }

    [Fact]
    public void A_repeated_request_does_not_extend_an_open_window()
    {
        // Requests arrive faster than a window lasts, so extending on each would mean the window
        // never closes and the hold never blocks again.
        var module = Downlink();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));
        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 3.0));

        // Still expires 3.5 s after the request that opened it.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 5.0)));
    }

    [Fact]
    public void An_uplink_module_still_watches_its_own_direction()
    {
        var module = new HoldModule("3074 UL", DestinyPorts.Peer, MeasuredDefaults.Peer3074);
        module.IsActive = true;

        // An inbound packet of trigger size must not start a resync on an uplink hold. 1258 is on
        // uplink's resync list, so it crosses only once a window is genuinely open.
        module.Decide(TestPackets.Inbound(3074, ReconnectRequest, 1.0));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 1258, 1.5)));

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 2.0));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 1258, 2.5)));
    }
}
