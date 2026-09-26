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

        // Inbound now flows so the peer can re-establish.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.5)));
    }

    [Fact]
    public void Blocking_resumes_after_the_resync_window()
    {
        var module = Downlink();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 4.0)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 4.6)));
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

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.5)));
        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Inbound(3074, Ordinary, 1.5, other)));
    }

    [Fact]
    public void A_repeated_request_extends_the_resync_rather_than_restarting_from_scratch()
    {
        // The game asks repeatedly while unanswered, so each request pushes the window out.
        var module = Downlink();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));
        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 3.0));

        // Would have expired at 4.5 from the first request alone.
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 5.0)));
    }

    [Fact]
    public void An_uplink_module_still_watches_its_own_direction()
    {
        var module = new HoldModule("3074 UL", DestinyPorts.Peer, MeasuredDefaults.Peer3074);
        module.IsActive = true;

        // An inbound packet of trigger size must not start a resync on an uplink hold.
        module.Decide(TestPackets.Inbound(3074, ReconnectRequest, 1.0));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 500, 1.5)));

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 2.0));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 500, 2.5)));
    }
}
