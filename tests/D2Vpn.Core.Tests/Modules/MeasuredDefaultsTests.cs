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
    public void Uplink_ships_a_measured_pass_list()
    {
        Assert.Equal([36, 51, 128, 136, 233, 1166], MeasuredDefaults.Peer3074.PassLengths.Order());
    }

    [Fact]
    public void Downlink_ships_no_pass_list_because_nothing_is_known_to_need_passing()
    {
        // Two runs with a reference limiter's resync enabled showed 51 and 233 crossing, and an
        // earlier version shipped them. A third run with that resync disabled passed nothing at
        // all for nineteen seconds, so those sizes were resync-window traffic, not a pass list.
        // Inventing a pass list from them would tell a module to leak packets for no reason.
        Assert.Empty(MeasuredDefaults.Peer3074Downlink.PassLengths);
    }

    [Fact]
    public void A_direction_with_no_pass_list_still_declares_its_direction()
    {
        Assert.Equal(HoldDirection.Downlink, MeasuredDefaults.Peer3074Downlink.Direction);
    }

    [Fact]
    public void Uplink_needs_no_ceiling_because_its_resync_is_proven()
    {
        // Holds have run past 20 minutes with the exchange being completed.
        Assert.NotEmpty(MeasuredDefaults.Peer3074.ResyncTriggerLengths);
        Assert.Null(MeasuredDefaults.Peer3074.SafetyTimeout);
    }

    [Fact]
    public void Downlink_keeps_a_ceiling_until_its_resync_is_proven_in_play()
    {
        // The trigger is observed but the module has never run against a live game, and a
        // downlink hold error-codes at about 30 seconds without a working resync.
        var down = MeasuredDefaults.Peer3074Downlink;

        Assert.NotEmpty(down.ResyncTriggerLengths);
        Assert.NotNull(down.SafetyTimeout);
        Assert.True(down.SafetyTimeout < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_trigger_travelling_the_held_direction_must_also_be_passed()
    {
        // Otherwise it would be dropped before it could be acted on. This does not apply to a
        // trigger on the other direction, which is passed untouched anyway.
        foreach (var options in new[] { MeasuredDefaults.Peer3074, MeasuredDefaults.Peer3074Downlink })
        {
            var triggerIsHeld = options.Direction switch
            {
                HoldDirection.Uplink => options.EffectiveTriggerDirection == PacketDirection.Outbound,
                HoldDirection.Downlink => options.EffectiveTriggerDirection == PacketDirection.Inbound,
                _ => true
            };

            if (triggerIsHeld)
            {
                Assert.All(options.ResyncTriggerLengths, t => Assert.Contains(t, options.PassLengths));
            }
        }
    }

    [Fact]
    public void Downlink_watches_outbound_for_its_trigger()
    {
        // Whichever direction is held, this machine is the one that notices a silent peer and
        // asks to reconnect, so the request is outbound either way.
        var down = MeasuredDefaults.Peer3074Downlink;

        Assert.Equal(HoldDirection.Downlink, down.Direction);
        Assert.Equal(PacketDirection.Outbound, down.EffectiveTriggerDirection);
        Assert.Contains(128, down.ResyncTriggerLengths);
    }

    [Fact]
    public void Uplink_watches_its_own_direction_by_default()
    {
        Assert.Null(MeasuredDefaults.Peer3074.ResyncTriggerDirection);
        Assert.Equal(PacketDirection.Outbound, MeasuredDefaults.Peer3074.EffectiveTriggerDirection);
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
