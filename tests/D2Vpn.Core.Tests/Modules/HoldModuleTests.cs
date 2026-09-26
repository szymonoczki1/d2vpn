using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Drives the module through whole holds using packet timestamps, with no driver and no game.
/// Behaviour and figures come from <c>docs/protocol.md</c>, findings 10 through 14b.
/// </summary>
public class HoldModuleTests
{
    private const int Ordinary = 500;
    private const int ReconnectRequest = 128;
    private const int HandshakeReply = 233;
    private const int DataPacket = 1166;

    private static HoldModule Module(HoldModuleOptions? options = null) =>
        new("PVE", DestinyPorts.Peer, options ?? MeasuredDefaults.Peer3074);

    private static PacketVerdict Out(HoldModule module, int length, double atSeconds) =>
        module.Decide(TestPackets.Outbound(3074, length, atSeconds));

    [Fact]
    public void An_inactive_module_passes_everything()
    {
        var module = Module();

        Assert.Equal(PacketVerdict.Pass, Out(module, Ordinary, 0));
        Assert.Equal(PacketVerdict.Pass, Out(module, ReconnectRequest, 1));
    }

    [Fact]
    public void While_holding_ordinary_uplink_is_dropped()
    {
        var module = Module();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 0));
        Assert.Equal(PacketVerdict.Drop, Out(module, 742, 0.1));
    }

    [Fact]
    public void While_holding_the_signature_sizes_still_pass()
    {
        // Blocking these kills the session. They are the whole technique. Each is checked on a
        // fresh module because two of them are resync triggers, and a module already resyncing
        // judges by the resync list rather than the standing one.
        foreach (var length in MeasuredDefaults.Peer3074.PassLengths)
        {
            var module = Module();
            module.IsActive = true;

            Assert.Equal(PacketVerdict.Pass, Out(module, length, 0));
        }
    }

    [Fact]
    public void Downlink_is_never_touched()
    {
        // The block is directional. Measured: downlink held a steady rate through every hold.
        var module = Module();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 1258, 1)));
    }

    [Fact]
    public void Traffic_on_another_port_group_is_ignored()
    {
        var module = Module();
        module.IsActive = true;

        // Same size, same direction, same moment - only the port differs.
        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 0));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(27056, Ordinary, 0)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(443, Ordinary, 0)));
    }

    [Fact]
    public void A_reconnect_request_starts_a_resync_and_traffic_flows_again()
    {
        // The finding this module exists for. A pass-list alone leaves the peer asking forever.
        var module = Module();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 0));

        Assert.Equal(PacketVerdict.Pass, Out(module, ReconnectRequest, 1.0));
        Assert.Equal(HoldState.Resyncing, module.PeerStates.Values.Single());

        // The handshake completes while the window is open; ordinary game state does not, which
        // is what stops a kill reaching the server during an uplink hold.
        Assert.Equal(PacketVerdict.Pass, Out(module, HandshakeReply, 2.0));
        Assert.Equal(PacketVerdict.Pass, Out(module, DataPacket, 3.0));
        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 1.5));
    }

    [Fact]
    public void Blocking_resumes_once_the_resync_window_has_passed()
    {
        var module = Module();
        module.IsActive = true;

        Out(module, ReconnectRequest, 1.0);

        // 190 is on the resync list only, so it crosses solely while the window is open.
        Assert.Equal(PacketVerdict.Pass, Out(module, 190, 4.0));

        // Default resync is 3.5 s, so by 4.6 s it is over.
        Assert.Equal(PacketVerdict.Drop, Out(module, 190, 4.6));
        Assert.Equal(HoldState.Blocking, module.PeerStates.Values.Single());
    }

    [Fact]
    public void A_long_hold_cycles_block_resync_block()
    {
        // The measured shape: a 20 s hold is not one continuous cut.
        var module = Module();
        module.IsActive = true;
        var states = new List<HoldState>();

        for (var t = 0.0; t < 20; t += 0.5)
        {
            // The game keeps asking while it is being blocked; it stops once traffic flows.
            var length = module.PeerStates.Values.FirstOrDefault() == HoldState.Resyncing
                ? Ordinary
                : ReconnectRequest;

            Out(module, length, t);
            states.Add(module.PeerStates.Values.Single());
        }

        Assert.Contains(HoldState.Blocking, states);
        Assert.Contains(HoldState.Resyncing, states);

        // Several cycles, not one long resync.
        var transitions = states.Zip(states.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(transitions >= 4, $"expected repeated cycling, saw {transitions} transitions");
    }

    [Fact]
    public void Each_peer_holds_independently()
    {
        // Peers run their own tick and reconnect on their own schedule, so one peer resyncing must
        // not lift the block on another.
        var module = Module();
        module.IsActive = true;

        var other = TestPackets.OtherPeer;

        module.Decide(TestPackets.Outbound(3074, ReconnectRequest, 1.0));

        // 190 is on the resync list: it crosses for the resyncing peer and not for the other.
        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Outbound(3074, 190, 1.5, other)));

        Assert.Equal(
            PacketVerdict.Pass,
            module.Decide(TestPackets.Outbound(3074, 190, 1.6)));
    }

    [Fact]
    public void Releasing_restores_traffic_immediately_and_forgets_everything()
    {
        var module = Module();
        module.IsActive = true;

        Out(module, ReconnectRequest, 1.0);
        Assert.NotEmpty(module.PeerStates);

        module.IsActive = false;

        Assert.Empty(module.PeerStates);
        Assert.Equal(PacketVerdict.Pass, Out(module, Ordinary, 2.0));
    }

    [Fact]
    public void Re_arming_after_a_release_starts_from_a_clean_state()
    {
        var module = Module();
        module.IsActive = true;
        Out(module, ReconnectRequest, 1.0);
        module.IsActive = false;

        module.IsActive = true;

        // A resync left over from the previous hold would wrongly pass this.
        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 2.0));
    }

    [Fact]
    public void A_safety_timeout_disables_the_module_rather_than_quietly_passing()
    {
        // 30k needs this; 3074 does not. Ceilings belong to modules, never to the app.
        var module = new HoldModule("30k", DestinyPorts.Instance, MeasuredDefaults.Instance30k);
        var fired = 0;
        module.SafetyTimeoutElapsed += _ => fired++;
        module.IsActive = true;

        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Outbound(30000, Ordinary, 0)));

        // Default limit is 20 s.
        Assert.Equal(
            PacketVerdict.Pass,
            module.Decide(TestPackets.Outbound(30000, Ordinary, 21)));

        Assert.False(module.IsActive);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_module_with_no_safety_timeout_holds_indefinitely()
    {
        // 3074 has run past 20 minutes with the reconnect exchange being completed.
        var module = Module();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 0));
        Assert.Equal(PacketVerdict.Drop, Out(module, Ordinary, 1800));
        Assert.True(module.IsActive);
    }

    [Fact]
    public void The_safety_clock_starts_at_the_first_packet_not_at_activation()
    {
        // Arming a module while nothing is flowing must not burn its budget.
        var module = new HoldModule("30k", DestinyPorts.Instance, MeasuredDefaults.Instance30k);
        module.IsActive = true;

        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Outbound(30000, Ordinary, 500)));

        Assert.Equal(
            PacketVerdict.Drop,
            module.Decide(TestPackets.Outbound(30000, Ordinary, 515)));

        Assert.True(module.IsActive);
    }

    [Fact]
    public void A_blank_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => new HoldModule(" ", DestinyPorts.Peer, MeasuredDefaults.Peer3074));
    }
}
