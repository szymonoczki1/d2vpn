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
    public void Downlink_needs_no_ceiling_now_that_its_resync_is_proven()
    {
        // Validated at 96% blocked over a 60 second hold - twice the ~30 s ceiling a hold without
        // a working resync dies at - with a pickup deferred until release.
        var down = MeasuredDefaults.Peer3074Downlink;

        Assert.NotEmpty(down.ResyncTriggerLengths);
        Assert.Null(down.SafetyTimeout);
    }

    [Fact]
    public void Downlink_answers_the_handshake_rather_than_opening_the_gate()
    {
        // A resync that stops blocking entirely leaks the state the hold exists to defer: one
        // packet carries a pickup. These are the sizes measured crossing a working resync.
        var down = MeasuredDefaults.Peer3074Downlink;

        Assert.NotNull(down.ResyncPassLengths);
        Assert.Equal([51, 233], down.ResyncPassLengths!.Order());
        Assert.Empty(down.PassLengths);
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

    [Fact]
    public void A_full_hold_passes_nothing_outside_a_resync()
    {
        // A module carries one pass list, and the two directions need different ones - uplink's
        // describes what this machine must send, downlink's what the peer must send to us.
        // Applying either to both would pass the wrong traffic one way, so a full hold passes
        // nothing and carries a timeout while it is unproven.
        var full = MeasuredDefaults.Peer3074Full;

        Assert.Equal(HoldDirection.Both, full.Direction);
        Assert.Empty(full.PassLengths);
        Assert.NotNull(full.SafetyTimeout);
    }

    [Theory]
    [InlineData(HoldDirection.Uplink)]
    [InlineData(HoldDirection.Downlink)]
    [InlineData(HoldDirection.Both)]
    public void Defaults_declare_the_direction_they_describe(HoldDirection direction)
    {
        var options = MeasuredDefaults.For(DestinyPorts.Peer, direction);

        Assert.NotNull(options);
        Assert.Equal(direction, options.Direction);
    }

    [Fact]
    public void Unmeasured_combinations_return_nothing_rather_than_a_guess()
    {
        // 7500 has no entry here at all because it is TCP and uses a different module; 30k has
        // only an uplink one, and its TCP path supersedes it.
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Api));
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Instance, HoldDirection.Downlink));
        Assert.Null(MeasuredDefaults.For(DestinyPorts.Instance, HoldDirection.Both));
    }

    [Fact]
    public void Every_27k_direction_has_a_measured_signature()
    {
        Assert.NotNull(MeasuredDefaults.For(DestinyPorts.Players));
        Assert.NotNull(MeasuredDefaults.For(DestinyPorts.Players, HoldDirection.Downlink));
        Assert.NotNull(MeasuredDefaults.For(DestinyPorts.Players, HoldDirection.Both));
    }

    [Fact]
    public void The_27k_resync_triggers_on_the_size_the_reference_uses()
    {
        // 1300 was read out of the reference implementation's source before 27k was calibrated
        // here, and then fell straight out of the calibration at 34x concentration. Independent
        // confirmation rather than a transcription.
        Assert.Equal([1300], MeasuredDefaults.Players27k.ResyncTriggerLengths.Order());
        Assert.Equal([1300], MeasuredDefaults.Players27kDownlink.ResyncTriggerLengths.Order());
    }

    /// <summary>Every uplink signature size from both 27k calibration runs.</summary>
    /// <remarks>
    /// The two runs share exactly one size, 1300. Listing them together is the point: a rule that
    /// only passes one run's sizes is fitted to that run, and would have passed nothing in the
    /// other.
    /// </remarks>
    private static readonly int[] Signature27k =
    [
        303, 305, 306, 373, 1185, 1300,                      // run 1
        6, 100, 237, 266, 337, 339, 349, 350, 396, 1300      // run 2
    ];

    [Fact]
    public void Every_measured_27k_size_crosses_a_resync_window()
    {
        foreach (var options in new[] { MeasuredDefaults.Players27k, MeasuredDefaults.Players27kFull })
        {
            foreach (var length in Signature27k)
            {
                var crosses = options.ResyncPassLengths!.Contains(length)
                    || length <= options.ResyncPassMaxLength
                    || options.PassLengths.Contains(length);

                Assert.True(crosses, $"{length} was measured crossing a 27k hold and must cross");
            }
        }
    }

    [Fact]
    public void The_27k_rule_is_a_threshold_plus_the_two_large_sizes()
    {
        var options = MeasuredDefaults.Players27k;

        // 396 is the largest small size observed and 1185 the smallest large one. The threshold
        // sits in the gap, so nothing between them crosses on size alone.
        Assert.Equal(400, options.ResyncPassMaxLength);
        Assert.Equal([1185, 1300], options.ResyncPassLengths!.Order());

        foreach (var ordinary in new[] { 401, 700, 1100, 1184, 1299, 1400 })
        {
            Assert.False(
                ordinary <= options.ResyncPassMaxLength
                    || options.ResyncPassLengths!.Contains(ordinary),
                $"{ordinary} is in the gap and must not cross");
        }

        // Nothing crosses outside a window, as on 3074.
        Assert.Empty(options.PassLengths);
    }

    [Fact]
    public void No_other_port_uses_the_27k_threshold()
    {
        // It leaks more than a list - run 1 measured 100-byte packets at 5% of normal traffic -
        // and is justified only where an exact list was shown not to replicate.
        Assert.Null(MeasuredDefaults.Peer3074.ResyncPassMaxLength);
        Assert.Null(MeasuredDefaults.Peer3074Downlink.ResyncPassMaxLength);
        Assert.Null(MeasuredDefaults.Instance30k.ResyncPassMaxLength);
    }

    [Fact]
    public void The_27k_downlink_narrows_nothing_because_nothing_was_measured()
    {
        // Calibrating it turned up one size, 9 bytes, and the tool called it ordinary at 2.8x -
        // 9-byte packets are 36% of normal inbound traffic, so passing them on length alone would
        // pass game state too. A null resync list means everything passes during a window.
        Assert.Null(MeasuredDefaults.Players27kDownlink.ResyncPassLengths);
        Assert.Empty(MeasuredDefaults.Players27kDownlink.PassLengths);
    }

    [Fact]
    public void Only_the_two_direction_27k_hold_carries_a_safety_timeout()
    {
        // With nothing flowing either way there is no traffic left to notice a problem in.
        Assert.Null(MeasuredDefaults.Players27k.SafetyTimeout);
        Assert.Null(MeasuredDefaults.Players27kDownlink.SafetyTimeout);
        Assert.NotNull(MeasuredDefaults.Players27kFull.SafetyTimeout);
    }
}
