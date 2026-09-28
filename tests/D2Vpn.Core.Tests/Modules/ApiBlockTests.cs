using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// A TCP block only works if it suppresses retransmissions, so most of these are about what
/// happens after the first segment is withheld rather than to it.
/// </summary>
public class ApiBlockTests
{
    private const int Port = 7500;

    private static ApiBlockModule Module(ApiBlockOptions? options = null)
    {
        var module = new ApiBlockModule("API Block", DestinyPorts.Api, options);
        module.IsActive = true;
        return module;
    }

    /// <summary>
    /// A module with ceilings on, for the tests that are about the ceilings.
    /// </summary>
    /// <remarks>
    /// They are off by default now - the effect lasts as long as the hold, so the length is the
    /// operator's call - but the machinery still has to work when switched on.
    /// </remarks>
    private static ApiBlockModule Capped(bool holdEverything = false) =>
        Module(ApiBlockOptions.Reference with { HoldEverything = holdEverything });

    /// <summary>A module on the 30k group, which is a different port range from 7500.</summary>
    private static ApiBlockModule Instance(ApiBlockOptions? options = null)
    {
        var module = new ApiBlockModule("30k", DestinyPorts.Instance, options);
        module.IsActive = true;
        return module;
    }

    /// <summary>The reference implementation's two-phase capture, which is no longer the default.</summary>
    private static ApiBlockModule Slicing() =>
        Module(ApiBlockOptions.Api7500 with { HoldEverything = false });

    /// <summary>
    /// Asserts a packet was withheld, without caring whether it was kept or destroyed.
    /// </summary>
    /// <remarks>
    /// Most of these tests are about what gets held back, not about its fate afterwards - and the
    /// fate changed when buffering became the default.
    /// </remarks>
    private static void AssertWithheld(PacketVerdict verdict) =>
        Assert.NotEqual(PacketVerdict.Pass, verdict);

    [Fact]
    public void An_idle_module_touches_nothing()
    {
        var module = new ApiBlockModule("API Block", DestinyPorts.Api);

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(Port, 500, 1000)));
    }

    [Fact]
    public void Outbound_is_never_touched()
    {
        // Blocking outbound would stop the acknowledgements this machine owes, which makes the peer
        // retransmit harder and reset sooner, for no gain any reference tool claims.
        var module = Module();

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpOutbound(Port, 500)));
    }

    [Fact]
    public void Traffic_outside_the_group_is_never_touched()
    {
        var module = Module();

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(3074, 500, 1000)));
    }

    [Fact]
    public void An_inbound_segment_carrying_data_is_withheld()
    {
        var module = Module();

        AssertWithheld(module.Decide(TestPackets.TcpData(Port, 500, 1000)));
        Assert.Equal(1, module.SegmentsWithheld);
    }

    [Fact]
    public void A_bare_acknowledgement_passes()
    {
        // It carries nothing, so withholding it stalls the conversation without holding anything.
        var module = Module();

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpAck(Port, 1000)));
        Assert.Equal(0, module.SegmentsWithheld);
    }

    [Fact]
    public void A_retransmission_of_a_withheld_segment_is_suppressed()
    {
        // The whole point. Dropping a TCP segment does not destroy it - the sender re-sends the
        // same data under the same sequence number, and a module that does not recognise that has
        // leaked about 200 ms after it started.
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        // Quiet for longer than the capture window, so the slice is now held rather than growing.
        var verdict = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0.3));

        Assert.Equal(PacketVerdict.Drop, verdict);
        Assert.Equal(1, module.RetransmitsSuppressed);
    }

    [Fact]
    public void New_data_arriving_after_the_slice_is_captured_passes()
    {
        // What makes the two-phase mode a hold on a fixed slice rather than on the connection.
        var module = Slicing();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        var verdict = module.Decide(TestPackets.TcpData(Port, 500, sequence: 9999, atSeconds: 0.3));

        Assert.Equal(PacketVerdict.Pass, verdict);
    }

    [Fact]
    public void Data_still_arriving_inside_the_capture_window_keeps_being_withheld()
    {
        // While the sender still has window, everything it sends joins the slice.
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0.00));
        var second = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1500, atSeconds: 0.01));
        var third = module.Decide(TestPackets.TcpData(Port, 500, sequence: 2000, atSeconds: 0.02));

        AssertWithheld(second);
        AssertWithheld(third);
        Assert.Equal(3, module.SegmentsWithheld);
    }

    [Fact]
    public void Capacity_is_recorded_once_the_slice_spans_the_capture_limit()
    {
        var module = Module();

        // Three 500-byte segments span 1500 bytes, past the 1460 default.
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0.00));
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1500, atSeconds: 0.01));
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 2000, atSeconds: 0.02));

        Assert.Equal(0, module.CapacityReached);

        // Capacity is noticed on a suppressed retransmission, once the slice is held.
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0.5));

        Assert.Equal(1, module.CapacityReached);
    }

    [Fact]
    public void A_slice_smaller_than_the_capture_limit_never_reaches_capacity()
    {
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 100, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpData(Port, 100, sequence: 1000, atSeconds: 0.5));

        Assert.Equal(1, module.RetransmitsSuppressed);
        Assert.Equal(0, module.CapacityReached);
    }

    [Fact]
    public void A_reset_disables_the_module_immediately()
    {
        // The failure mode on this port is a reset and an error code, not a slow timeout. Holding
        // past one achieves nothing on a connection that is already gone.
        var module = Module();
        var reasons = new List<string>();
        module.Disabled += (_, why) => reasons.Add(why);

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000));
        var verdict = module.Decide(TestPackets.TcpReset(Port, atSeconds: 1));

        Assert.Equal(PacketVerdict.Pass, verdict);
        Assert.False(module.IsActive);
        Assert.Equal(1, module.ResetsSeen);
        Assert.Single(reasons);
    }

    [Fact]
    public void The_hold_ends_at_the_maximum()
    {
        var module = Capped();
        string? reason = null;
        module.Disabled += (_, why) => reason = why;

        Capture(module, at: 0);

        // The peer keeps retransmitting throughout, so the quiet limit never trips and this test
        // isolates the maximum. Without them the hold ends at ~25 s on the quiet path instead,
        // which is the correct behaviour and simply a different limit.
        for (var t = 5.0; t <= 50; t += 5)
        {
            module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: t));
        }

        Assert.True(module.IsActive);

        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 60));

        Assert.False(module.IsActive);
        Assert.Contains("past the", reason);
    }

    [Fact]
    public void The_hold_ends_early_when_the_peer_stops_retransmitting()
    {
        // A peer that has gone quiet gave up on the data rather than on the connection, so there is
        // nothing left to withhold and continuing only spends budget before a reset.
        var module = Capped();
        string? reason = null;
        module.Disabled += (_, why) => reason = why;

        Capture(module, at: 0);

        // Past QuietHold (19 s) and past QuietRetransmit (24 s) since the last suppression.
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 30));

        Assert.False(module.IsActive);
        Assert.Contains("no retransmission", reason);
    }

    [Fact]
    public void A_peer_that_keeps_retransmitting_does_not_trigger_the_quiet_limit()
    {
        var module = Capped();

        Capture(module, at: 0);

        // Retransmissions every 5 s keep the connection from ever looking quiet.
        for (var t = 5.0; t < 45; t += 5)
        {
            module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: t));
        }

        Assert.True(module.IsActive);
        Assert.True(module.RetransmitsSuppressed > 5);
    }

    [Fact]
    public void Outbound_traffic_alone_can_end_the_hold()
    {
        // While holding, inbound may fall silent entirely. Outbound is then the only thing still
        // arriving to notice the ceiling has been reached, which is why it is checked before the
        // direction gate rather than after it.
        var module = Capped();

        Capture(module, at: 0);

        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 60));

        Assert.False(module.IsActive);
    }

    [Fact]
    public void Toggling_off_forgets_the_withheld_slice()
    {
        // A sequence number withheld during one hold means nothing during the next: the connection
        // has moved on, and matching against it would suppress whatever lands on that number.
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Toggle();
        module.Toggle();

        // Same sequence number, but it is new data as far as this hold is concerned.
        var verdict = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 5));

        AssertWithheld(verdict);
        Assert.Equal(2, module.SegmentsWithheld);
    }

    [Fact]
    public void Connections_are_tracked_separately()
    {
        // Two connections to the same service carry unrelated sequence spaces, so a number
        // withheld on one says nothing about the other.
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        var other = TestPackets.TcpData(
            Port, 500, sequence: 1000, atSeconds: 0.3, peer: TestPackets.OtherPeer);

        AssertWithheld(module.Decide(other));
        Assert.Equal(2, module.SegmentsWithheld);
    }

    [Fact]
    public void Buffering_changes_the_verdict_without_changing_what_is_blocked()
    {
        var module = Module(new ApiBlockOptions { Buffering = true });

        Assert.Equal(PacketVerdict.Buffer, module.Decide(TestPackets.TcpData(Port, 500, 1000)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpAck(Port, 2000)));
    }

    [Fact]
    public void A_retransmission_is_destroyed_even_when_buffering()
    {
        // The peer is re-sending data already queued, so keeping it would replay the same bytes
        // several times. A run holding four segments replayed fifteen packets before this.
        var module = Module(new ApiBlockOptions { Buffering = true });

        var first = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        var again = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0.3));

        Assert.Equal(PacketVerdict.Buffer, first);
        Assert.Equal(PacketVerdict.Drop, again);
    }

    [Fact]
    public void Hold_all_keeps_withholding_new_data_after_the_capture_window()
    {
        // The default captures a slice and passes everything later, which on a channel quieter
        // than the capture window means exactly one segment is ever withheld. Seven consecutive
        // holds in game each withheld one.
        var module = Module(new ApiBlockOptions { HoldEverything = true });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        // Well past the 100 ms window, and genuinely new data rather than a retransmission.
        var later = module.Decide(TestPackets.TcpData(Port, 500, sequence: 9999, atSeconds: 5));

        AssertWithheld(later);
        Assert.Equal(2, module.SegmentsWithheld);
    }

    [Fact]
    public void Without_hold_all_new_data_passes_once_the_window_closes()
    {
        // The behaviour --capture-slice selects, pinned so the difference stays visible.
        var module = Slicing();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        var later = module.Decide(TestPackets.TcpData(Port, 500, sequence: 9999, atSeconds: 5));

        Assert.Equal(PacketVerdict.Pass, later);
        Assert.Equal(1, module.SegmentsWithheld);
    }

    [Fact]
    public void Hold_all_still_suppresses_retransmissions()
    {
        var module = Module(new ApiBlockOptions { HoldEverything = true });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        var again = module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 5));

        Assert.Equal(PacketVerdict.Drop, again);
        Assert.Equal(1, module.SegmentsWithheld);
        Assert.Equal(1, module.RetransmitsSuppressed);
    }

    [Fact]
    public void Hold_all_still_has_a_ceiling()
    {
        // It matters more here, not less: withholding everything closes the receive window, so the
        // reset arrives sooner than under the default.
        var module = Capped(holdEverything: true);

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 60));

        Assert.False(module.IsActive);
    }

    [Fact]
    public void Hold_all_still_passes_bare_acknowledgements()
    {
        var module = Module(new ApiBlockOptions { HoldEverything = true });

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpAck(Port, 1000)));
    }

    [Fact]
    public void A_zero_maximum_removes_that_ceiling()
    {
        // Deliberately allowed: every one of these numbers is inherited rather than measured, and
        // running past the guess is the only way to find where the real limit is.
        var module = Module(ApiBlockOptions.Reference with { MaxHold = TimeSpan.Zero });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        // Retransmissions throughout, so the quiet path cannot end it either.
        for (var t = 5.0; t <= 300; t += 5)
        {
            module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: t));
        }

        Assert.True(module.IsActive);
    }

    [Fact]
    public void A_zero_quiet_hold_removes_the_early_release()
    {
        var module = Module(ApiBlockOptions.Reference with { QuietHold = TimeSpan.Zero });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));

        // Silent well past QuietRetransmit, but inside the 54 s maximum.
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 40));

        Assert.True(module.IsActive);
    }

    [Fact]
    public void Both_ceilings_off_means_the_hold_never_ends_on_its_own()
    {
        var module = Module(new ApiBlockOptions
        {
            MaxHold = TimeSpan.Zero,
            QuietHold = TimeSpan.Zero
        });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 600));

        Assert.True(module.IsActive);
    }

    [Fact]
    public void A_reset_still_disables_the_module_with_no_ceiling()
    {
        // The one exit that is not a timer, so it must survive turning the timers off - the peer
        // resetting is the connection actually being gone, not a precaution.
        var module = Module(new ApiBlockOptions
        {
            MaxHold = TimeSpan.Zero,
            QuietHold = TimeSpan.Zero
        });

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpReset(Port, atSeconds: 600));

        Assert.False(module.IsActive);
        Assert.Equal(1, module.ResetsSeen);
    }

    [Fact]
    public void The_reference_preset_keeps_the_original_behaviour()
    {
        Assert.False(ApiBlockOptions.Reference.HoldEverything);
        Assert.False(ApiBlockOptions.Reference.Buffering);
    }

    [Fact]
    public void Buffering_and_holding_everything_are_both_on_by_default()
    {
        // Both measured. A 61 s hold replayed all three withheld segments, every one needing its
        // acknowledgement brought forward, and recovered visibly faster than waiting for the
        // sender to re-send. The two-phase capture withheld one segment per hold on this channel.
        Assert.True(ApiBlockOptions.Api7500.Buffering);
        Assert.True(ApiBlockOptions.Api7500.HoldEverything);
    }

    [Fact]
    public void The_hold_is_uncapped_by_default()
    {
        // The effect lasts as long as the hold does - the game is deprived of API data for every
        // second it runs - so how long to hold is the operator's call. The only automatic exit is
        // a reset, which is the connection already being gone rather than a precaution.
        var options = ApiBlockOptions.Api7500;

        Assert.Equal(TimeSpan.Zero, options.MaxHold);
        Assert.Equal(TimeSpan.Zero, options.QuietHold);
    }

    [Fact]
    public void The_capture_values_still_come_from_the_reference_implementation()
    {
        var options = ApiBlockOptions.Api7500;

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.CaptureQuiet);
        Assert.Equal(1460, options.CaptureBytes);
    }

    [Fact]
    public void The_reference_preset_keeps_the_inherited_ceilings()
    {
        var options = ApiBlockOptions.Reference;

        Assert.Equal(TimeSpan.FromSeconds(54), options.MaxHold);
        Assert.Equal(TimeSpan.FromSeconds(19), options.QuietHold);
        Assert.Equal(TimeSpan.FromSeconds(24), options.QuietRetransmit);
    }

    [Fact]
    public void An_uncapped_hold_still_ends_on_a_reset()
    {
        // The one exit that survives every setting, because it is not a timer.
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpReset(Port, atSeconds: 600));

        Assert.False(module.IsActive);
    }

    [Fact]
    public void An_uncapped_hold_runs_indefinitely_otherwise()
    {
        var module = Module();

        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 3600));

        Assert.True(module.IsActive);
    }

    [Fact]
    public void A_hold_that_never_reaches_the_capture_limit_still_has_a_ceiling()
    {
        // The bug this exists for. The ceiling used to be measured from the capture limit being
        // reached, and a 231 s run in game reached it zero times - 7500 segments are small and the
        // slice never spans 1460 bytes - so the timers never armed and a 92 s hold ran until the
        // peer reset it. A ceiling gated on a condition the traffic does not meet is not a ceiling.
        var module = Capped();
        string? reason = null;
        module.Disabled += (_, why) => reason = why;

        // One small segment: nowhere near 1460 bytes, so capacity is never reached.
        module.Decide(TestPackets.TcpData(Port, 200, sequence: 1000, atSeconds: 0));

        // Retransmissions throughout, so only the maximum can end this.
        for (var t = 5.0; t <= 50; t += 5)
        {
            module.Decide(TestPackets.TcpData(Port, 200, sequence: 1000, atSeconds: t));
        }

        Assert.Equal(0, module.CapacityReached);
        Assert.True(module.IsActive);

        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 60));

        Assert.False(module.IsActive);
        Assert.Equal(0, module.CapacityReached);
        Assert.Contains("past the", reason);
    }

    [Fact]
    public void A_quiet_peer_ends_a_hold_that_never_reached_the_capture_limit()
    {
        var module = Capped();
        string? reason = null;
        module.Disabled += (_, why) => reason = why;

        module.Decide(TestPackets.TcpData(Port, 200, sequence: 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 30));

        Assert.False(module.IsActive);
        Assert.Contains("no retransmission", reason);
    }

    [Fact]
    public void The_ceiling_runs_from_the_first_withheld_segment()
    {
        // Not from activation: a module toggled on while the connection is idle has withheld
        // nothing, and there is nothing yet for a peer to lose patience over.
        var module = Capped();

        module.Decide(TestPackets.TcpAck(Port, 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 100));

        Assert.True(module.IsActive);
    }

    // ---- the keepalive, which is what a resync is on a stream ----

    [Fact]
    public void A_keepalive_sized_segment_passes_however_long_the_hold_runs()
    {
        // Measured on 30k: inbound is a 73-byte heartbeat every ~3s, and blocking it freezes the
        // acknowledgement and kills the connection in about twenty seconds. Passing it lets a hold
        // run indefinitely, which is what the reference limiter's autoresync does.
        var module = Instance(ApiBlockOptions.Instance30k);

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(30000, 73, 1000)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.TcpData(30000, 73, 2000, 200)));
        Assert.Equal(2, module.KeepalivesPassed);
    }

    [Fact]
    public void Everything_that_is_not_the_keepalive_is_still_withheld()
    {
        // Otherwise the module would be doing nothing at all while it is on.
        var module = Instance(ApiBlockOptions.Instance30k);

        AssertWithheld(module.Decide(TestPackets.TcpData(30000, 500, 1000)));
        AssertWithheld(module.Decide(TestPackets.TcpData(30000, 72, 2000)));
        AssertWithheld(module.Decide(TestPackets.TcpData(30000, 74, 3000)));
        Assert.Equal(0, module.KeepalivesPassed);
        Assert.Equal(3, module.SegmentsWithheld);
    }

    [Fact]
    public void A_keepalive_never_enters_the_withheld_set()
    {
        // It passes before anything else looks at it, so its own retransmissions never need
        // suppressing - a repeat is just another keepalive.
        var module = Instance(ApiBlockOptions.Instance30k);

        module.Decide(TestPackets.TcpData(30000, 73, sequence: 1000, atSeconds: 0));
        var again = module.Decide(TestPackets.TcpData(30000, 73, sequence: 1000, atSeconds: 1));

        Assert.Equal(PacketVerdict.Pass, again);
        Assert.Equal(0, module.RetransmitsSuppressed);
        Assert.Equal(0, module.SegmentsWithheld);
    }

    [Fact]
    public void Clearing_the_pass_list_makes_the_hold_total()
    {
        // What --no-resync does, and on 30k it is the difference between holding indefinitely and
        // the game giving up after about twenty seconds.
        var module = Instance(ApiBlockOptions.Instance30k with { PassLengths = new HashSet<int>() });

        AssertWithheld(module.Decide(TestPackets.TcpData(30000, 73, 1000)));
        Assert.Equal(0, module.KeepalivesPassed);
    }

    [Fact]
    public void The_30k_presets_pass_the_measured_heartbeat()
    {
        Assert.Contains(73, ApiBlockOptions.Instance30k.PassLengths);
        Assert.Contains(73, ApiBlockOptions.Instance30kSlow.PassLengths);
    }

    [Fact]
    public void The_7500_preset_passes_nothing()
    {
        // It survives about 87s unaided and no keepalive has been identified on it.
        Assert.Empty(ApiBlockOptions.Api7500.PassLengths);
    }

    // ---- delay line: 30k slow ----

    [Fact]
    public void A_delay_line_still_withholds_and_still_suppresses_retransmissions()
    {
        // Without suppression the sender would simply deliver the segment early and there would be
        // no delay at all - the queue would hold a copy of something the receiver already had.
        var module = Instance(ApiBlockOptions.Instance30kSlow);

        var first = module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 0));
        var again = module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 1));

        Assert.Equal(PacketVerdict.Buffer, first);
        Assert.Equal(PacketVerdict.Drop, again);
        Assert.Equal(1, module.RetransmitsSuppressed);
    }

    [Fact]
    public void A_delay_line_forgets_segments_well_past_their_release()
    {
        // A hold forgets everything on untick, so its set cannot grow. A delay line never has that
        // release, so without ageing them out the set would grow for as long as the module is on.
        var module = Instance(ApiBlockOptions.Instance30kSlow);

        for (var t = 0; t < 40; t++)
        {
            module.Decide(TestPackets.TcpData(30000, 100, sequence: (uint)(1000 + t * 100), atSeconds: t));
        }

        // Every segment is withheld, but only the recent ones are still remembered.
        Assert.Equal(40, module.SegmentsWithheld);

        // An old sequence is no longer recognised as a retransmission, so it counts as new.
        module.Decide(TestPackets.TcpData(30000, 100, sequence: 1000, atSeconds: 40));

        Assert.Equal(41, module.SegmentsWithheld);
        Assert.Equal(0, module.RetransmitsSuppressed);
    }

    [Fact]
    public void A_recent_segment_is_still_recognised_under_a_delay_line()
    {
        // The pruning must not be so eager that it forgets a segment still inside its delay, which
        // would let the sender's retransmission through and defeat the whole thing.
        var module = Instance(ApiBlockOptions.Instance30kSlow);

        module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 0));
        var again = module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 5));

        Assert.Equal(PacketVerdict.Drop, again);
        Assert.Equal(1, module.RetransmitsSuppressed);
    }

    [Fact]
    public void The_30k_slow_preset_delays_by_six_seconds()
    {
        // The reference limiter's own tooltip: "delays inbound 30k updates by 6 seconds while
        // active". Finding 7 measured gaps of 3.91 s and 5.94 s while it ran.
        Assert.Equal(TimeSpan.FromSeconds(6), ApiBlockOptions.Instance30kSlow.ReleaseAfter);
    }

    [Fact]
    public void The_30k_block_preset_holds_until_untick()
    {
        // "blocks inbound 30k updates until deactivated" - no release of its own.
        Assert.Null(ApiBlockOptions.Instance30k.ReleaseAfter);
        Assert.True(ApiBlockOptions.Instance30k.HoldEverything);
    }

    [Fact]
    public void A_block_never_forgets_a_segment_while_the_hold_runs()
    {
        // The contrast with a delay line. A hold has to keep suppressing the same segment for as
        // long as it runs, however long that is.
        var module = Instance(ApiBlockOptions.Instance30k);

        module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 0));
        var muchLater = module.Decide(TestPackets.TcpData(30000, 500, sequence: 1000, atSeconds: 300));

        Assert.Equal(PacketVerdict.Drop, muchLater);
        Assert.Equal(1, module.RetransmitsSuppressed);
    }

    /// <summary>Withholds enough to span the capture limit and establishes the hold.</summary>
    private static void Capture(ApiBlockModule module, double at)
    {
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: at));
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1500, atSeconds: at + 0.01));
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 2000, atSeconds: at + 0.02));
        module.Decide(TestPackets.TcpData(Port, 500, sequence: 1000, atSeconds: at + 0.5));
    }
}
