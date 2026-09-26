using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Pins the measured defaults so a change to them has to be deliberate. Every value came off one
/// machine and is meant to be overridden by calibration; see <c>docs/protocol.md</c>.
/// </summary>
public class MeasuredDefaultsTests
{
    [Fact]
    public void Uplink_and_downlink_signatures_are_different_sets()
    {
        // Neither can be derived from the other: uplink is what this machine must still be able to
        // send, downlink is what the peer must still be able to send to us.
        var up = MeasuredDefaults.Peer3074.PassLengths;
        var down = MeasuredDefaults.Peer3074Downlink.PassLengths;

        Assert.NotEqual(up.Order(), down.Order());
        Assert.Equal([36, 51, 128, 136, 233, 1166], up.Order());
        Assert.Equal([51, 233], down.Order());
    }

    [Fact]
    public void The_two_directions_share_part_of_a_handshake()
    {
        var shared = MeasuredDefaults.Peer3074.PassLengths
            .Intersect(MeasuredDefaults.Peer3074Downlink.PassLengths)
            .Order();

        Assert.Equal([51, 233], shared);
    }

    [Fact]
    public void Thirty_six_is_uplink_only_and_was_rejected_for_downlink()
    {
        // Measured at 2.4x enrichment on downlink, which is ordinary traffic rather than signature.
        Assert.Contains(36, MeasuredDefaults.Peer3074.PassLengths);
        Assert.DoesNotContain(36, MeasuredDefaults.Peer3074Downlink.PassLengths);
    }

    [Fact]
    public void A_direction_without_a_known_resync_trigger_carries_a_safety_timeout()
    {
        // Uplink knows its trigger (128/136) and needs no ceiling - holds have run past 20 minutes.
        // Downlink's is unmeasured, so a hold there would block without ever letting the peer
        // re-establish, and must not be allowed to run indefinitely.
        var up = MeasuredDefaults.Peer3074;
        var down = MeasuredDefaults.Peer3074Downlink;

        Assert.NotEmpty(up.ResyncTriggerLengths);
        Assert.Null(up.SafetyTimeout);

        Assert.Empty(down.ResyncTriggerLengths);
        Assert.NotNull(down.SafetyTimeout);
    }

    [Fact]
    public void Resync_triggers_are_always_a_subset_of_the_pass_list()
    {
        // A trigger that is not itself passed could never be seen, because it would be dropped.
        foreach (var options in new[] { MeasuredDefaults.Peer3074, MeasuredDefaults.Peer3074Downlink })
        {
            Assert.All(options.ResyncTriggerLengths, t => Assert.Contains(t, options.PassLengths));
        }
    }

    [Theory]
    [InlineData(HoldDirection.Uplink)]
    [InlineData(HoldDirection.Downlink)]
    public void Defaults_declare_the_direction_they_describe(HoldDirection direction)
    {
        var options = MeasuredDefaults.For(DestinyPorts.Peer, direction);

        Assert.NotNull(options);
        Assert.Equal(direction, options.Direction);
    }

    [Fact]
    public void Unmeasured_combinations_return_nothing_rather_than_a_guess()
    {
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Players));
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Api));
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Instance, HoldDirection.Downlink));
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Peer, HoldDirection.Both));
    }
}
