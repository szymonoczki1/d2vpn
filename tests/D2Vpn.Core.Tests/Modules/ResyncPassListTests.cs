using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// The standing pass list and the resync list are additive. Treating the resync list as a
/// replacement meant a reconnect request opened a window and was then dropped by it, so the
/// request never reached the peer it was meant to reach.
/// </summary>
public class ResyncPassListTests
{
    private static HoldModule Uplink() =>
        new("3074 UL", DestinyPorts.Peer, MeasuredDefaults.Peer3074);

    [Fact]
    public void A_trigger_is_not_dropped_by_the_window_it_opens()
    {
        var module = Uplink();
        module.IsActive = true;

        // 128 opens a window and is not on the resync list, only the standing one.
        Assert.DoesNotContain(128, MeasuredDefaults.Peer3074.ResyncPassLengths!);
        Assert.Contains(128, MeasuredDefaults.Peer3074.PassLengths);

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 128, 1.0)));
    }

    [Fact]
    public void Standing_pass_sizes_keep_crossing_while_a_window_is_open()
    {
        var module = Uplink();
        module.IsActive = true;
        module.Decide(TestPackets.Outbound(3074, 128, 1.0));

        foreach (var length in MeasuredDefaults.Peer3074.PassLengths)
        {
            Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, length, 1.5)));
        }
    }

    [Fact]
    public void Resync_only_sizes_cross_solely_while_a_window_is_open()
    {
        var module = Uplink();
        module.IsActive = true;

        // Blocked before any window.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 190, 0.5)));

        module.Decide(TestPackets.Outbound(3074, 128, 1.0));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 190, 1.5)));

        // And blocked again once it closes.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 190, 5.0)));
    }

    [Fact]
    public void Ordinary_game_state_never_crosses_either_way()
    {
        // This is what stops a kill reaching the server during an uplink hold.
        var module = Uplink();
        module.IsActive = true;
        module.Decide(TestPackets.Outbound(3074, 128, 1.0));

        foreach (var ordinary in new[] { 214, 478, 726, 742 })
        {
            Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, ordinary, 1.5)));
        }
    }
}
