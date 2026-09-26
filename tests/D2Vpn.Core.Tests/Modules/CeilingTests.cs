using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// A 3074 uplink hold survives only because something renews peer liveness while it runs. Remove
/// that and the game gives up at around 30 seconds - which is invisible when every hold is three
/// seconds long, and fatal the first time one is not.
/// </summary>
public class CeilingTests
{
    private static HoldModule Module(HoldModuleOptions options) =>
        new("PVE", DestinyPorts.Peer, options);

    [Fact]
    public void The_shipped_uplink_default_can_survive_a_long_hold()
    {
        // Either a resync trigger or a safety timeout. With neither, a hold runs until the game
        // error codes, and the shipped default must not be that configuration.
        var options = MeasuredDefaults.Peer3074;

        Assert.True(options.ResyncTriggerLengths.Count > 0 || options.SafetyTimeout is not null);
    }

    [Fact]
    public void Clearing_the_triggers_means_no_resync_can_ever_start()
    {
        // What --no-resync does. Recorded because a session log showed triggers=[(nothing)] while
        // the hold was being blamed on the packet buffer, which cannot act during a hold at all.
        var module = Module(MeasuredDefaults.Peer3074 with
        {
            ResyncTriggerLengths = new HashSet<int>()
        });
        module.IsActive = true;

        // Feed the sizes that would otherwise trigger one, for longer than any resync window.
        for (var t = 0; t < 40; t++)
        {
            module.Decide(TestPackets.Outbound(3074, 128, t));
            module.Decide(TestPackets.Outbound(3074, 136, t + 0.5));
        }

        Assert.Equal(0, module.TriggersSeen);
        Assert.Equal(0, module.ResyncsStarted);
    }

    [Fact]
    public void Clearing_the_pass_list_leaves_the_resync_working()
    {
        // The reason --no-pass alone is the right setting for a short sequence: it stops the only
        // thing that can leak, without disarming the only thing that saves a long hold.
        var module = Module(MeasuredDefaults.Peer3074 with { PassLengths = new HashSet<int>() });
        module.IsActive = true;

        var trigger = MeasuredDefaults.Peer3074.ResyncTriggerLengths.First();
        module.Decide(TestPackets.Outbound(3074, trigger, 0));

        Assert.Equal(1, module.TriggersSeen);
        Assert.Equal(1, module.ResyncsStarted);
    }

    [Fact]
    public void A_total_block_still_blocks_everything_outside_a_resync_window()
    {
        var module = Module(MeasuredDefaults.Peer3074 with { PassLengths = new HashSet<int>() });
        module.IsActive = true;

        // 500 is ordinary traffic: not a trigger, not a handshake size.
        Assert.NotEqual(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 500, 0)));
    }
}
