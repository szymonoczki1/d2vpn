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

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.TcpData(Port, 500, 1000)));
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
        // What makes this a hold on a fixed slice rather than a block on the whole connection.
        var module = Module();

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

        Assert.Equal(PacketVerdict.Drop, second);
        Assert.Equal(PacketVerdict.Drop, third);
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
        var module = Module();
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
        var module = Module();
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
        var module = Module();

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
        var module = Module();

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

        Assert.Equal(PacketVerdict.Drop, verdict);
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

        Assert.Equal(PacketVerdict.Drop, module.Decide(other));
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

        Assert.Equal(PacketVerdict.Drop, later);
        Assert.Equal(2, module.SegmentsWithheld);
    }

    [Fact]
    public void Without_hold_all_new_data_passes_once_the_window_closes()
    {
        // The behaviour hold-all exists to override, pinned so the difference stays visible.
        var module = Module();

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
        var module = Module(new ApiBlockOptions { HoldEverything = true });

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
    public void Hold_all_is_off_by_default()
    {
        Assert.False(ApiBlockOptions.Api7500.HoldEverything);
    }

    [Fact]
    public void Buffering_is_off_by_default()
    {
        // The sender keeps its own copy and retransmits it, so nothing is lost by dropping - and a
        // replayed segment would carry a stale acknowledgement number this project cannot yet
        // rewrite.
        Assert.False(ApiBlockOptions.Api7500.Buffering);
    }

    [Fact]
    public void The_defaults_are_the_reference_implementation_values()
    {
        var options = ApiBlockOptions.Api7500;

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.CaptureQuiet);
        Assert.Equal(1460, options.CaptureBytes);
        Assert.Equal(TimeSpan.FromSeconds(54), options.MaxHold);
        Assert.Equal(TimeSpan.FromSeconds(19), options.QuietHold);
        Assert.Equal(TimeSpan.FromSeconds(24), options.QuietRetransmit);
    }

    [Fact]
    public void This_port_always_has_a_ceiling_unlike_3074_uplink()
    {
        // A 3074 uplink hold survived 451 s across 35 resync cycles. Nothing equivalent protects a
        // TCP connection, so a hold here always ends in a reset if left alone.
        Assert.True(ApiBlockOptions.Api7500.MaxHold > TimeSpan.Zero);
    }

    [Fact]
    public void A_hold_that_never_reaches_the_capture_limit_still_has_a_ceiling()
    {
        // The bug this exists for. The ceiling used to be measured from the capture limit being
        // reached, and a 231 s run in game reached it zero times - 7500 segments are small and the
        // slice never spans 1460 bytes - so the timers never armed and a 92 s hold ran until the
        // peer reset it. A ceiling gated on a condition the traffic does not meet is not a ceiling.
        var module = Module();
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
        var module = Module();
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
        var module = Module();

        module.Decide(TestPackets.TcpAck(Port, 1000, atSeconds: 0));
        module.Decide(TestPackets.TcpOutbound(Port, 10, atSeconds: 100));

        Assert.True(module.IsActive);
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
