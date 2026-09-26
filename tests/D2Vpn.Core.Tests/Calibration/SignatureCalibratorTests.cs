using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Calibration;

/// <summary>
/// Driven by capture <c>pve-toggle-01</c>: 60 s on UDP 3074 with a module holding uplink from
/// roughly s8.7 to s19.6. Figures come from <c>docs/protocol.md</c>.
/// </summary>
public class SignatureCalibratorTests
{
    /// <summary>Outbound payload sizes during normal play. A steady 10 Hz tick.</summary>
    private static readonly int[] SteadyStateSizes =
    [
        446, 454, 462, 470, 478, 486, 494, 502, 510, 518, 526, 534,
        542, 550, 558, 566, 574, 582, 598, 614, 622, 638, 670, 1142
    ];

    /// <summary>
    /// The only uplink packets that crossed during the hold, at their measured offsets. Three
    /// small control packets, then a handshake burst inside a single second.
    /// </summary>
    private static readonly (double At, int Length)[] EscapedDuringBlock =
    [
        (12.629, 136),
        (13.049, 128),
        (14.327, 51),
        (14.775, 1166),
        (14.829, 190),
        (14.927, 198),
        (15.020, 198)
    ];

    /// <summary>Replays the measured capture: 10 Hz baseline, the hold, then the release burst.</summary>
    private static List<PacketInfo> RealCaptureTimeline()
    {
        var packets = new List<PacketInfo>();

        // s0-8: steady 10 packets/second each way.
        for (var second = 0; second < 9; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                var at = second + (tick * 0.1);
                var size = SteadyStateSizes[(second * 10 + tick) % SteadyStateSizes.Length];
                packets.Add(TestPackets.Outbound(3074, size, at));
                packets.Add(TestPackets.Inbound(3074, 1258, at));
            }
        }

        // s9-19: uplink held. Downlink continues untouched, which is what makes this a
        // directional block rather than a dead connection.
        foreach (var (at, length) in EscapedDuringBlock)
        {
            packets.Add(TestPackets.Outbound(3074, length, at));
        }

        for (var second = 9; second < 19; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                packets.Add(TestPackets.Inbound(3074, 1258, second + (tick * 0.1)));
            }
        }

        // s19.641: the buffer drains as a hard dump, then traffic recovers.
        for (var i = 0; i < 59; i++)
        {
            packets.Add(TestPackets.Outbound(3074, 198 + (i % 5 * 8), 19.641));
        }

        for (var second = 20; second < 26; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                var at = second + (tick * 0.1);
                packets.Add(TestPackets.Outbound(3074, SteadyStateSizes[tick], at));
                packets.Add(TestPackets.Inbound(3074, 1258, at));
            }
        }

        return packets;
    }

    private static CalibrationResult Calibrate(IEnumerable<PacketInfo> packets)
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);
        foreach (var packet in packets)
        {
            calibrator.Observe(packet);
        }

        return calibrator.Build();
    }

    [Fact]
    public void Finds_the_block_window_without_being_told_when_it_happened()
    {
        var result = Calibrate(RealCaptureTimeline());

        Assert.True(result.BlockDetected);
        Assert.Equal(10, result.Peers[0].BaselineOutboundRate, precision: 1);

        // The start sits on the last packet of normal traffic (8.9s), not the bucket edge at 9.0.
        Assert.InRange(result.CombinedWindow!.Start.TotalSeconds, 8.8, 9.01);
        Assert.InRange(result.CombinedWindow!.Duration.TotalSeconds, 10.0, 10.2);
    }

    [Fact]
    public void Normal_packets_before_the_keypress_are_kept_out_of_the_signature()
    {
        // A hold starts partway through a second, so the opening bucket carries ordinary traffic
        // first and the hold after. Without refining the boundary those strays enter the signature
        // as sizes that are plainly normal game state - which is exactly what happened in a
        // measured run, where three packets in the opening bucket produced three bogus sizes.
        var packets = new List<PacketInfo>();

        for (var second = 0; second < 12; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500 + (tick % 4 * 8), second + (tick * 0.05)));
            }
        }

        // s12.00-12.30: still normal, at normal spacing, with distinctive sizes.
        packets.Add(TestPackets.Outbound(3074, 561, 12.00));
        packets.Add(TestPackets.Outbound(3074, 563, 12.10));
        packets.Add(TestPackets.Outbound(3074, 565, 12.20));

        // The key goes down around 12.3; from here only handshake packets cross.
        packets.Add(TestPackets.Outbound(3074, 128, 14.10));
        packets.Add(TestPackets.Outbound(3074, 136, 15.40));
        packets.Add(TestPackets.Outbound(3074, 1166, 17.90));
        packets.Add(TestPackets.Outbound(3074, 51, 19.20));

        for (var second = 22; second < 30; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500 + (tick % 4 * 8), second + (tick * 0.05)));
            }
        }

        var result = Calibrate(packets);

        Assert.True(result.BlockDetected);
        Assert.Equal([51, 128, 136, 1166], result.OutboundSignature);

        // The three pre-keypress packets belong to normal traffic, not to the hold.
        Assert.DoesNotContain(561, result.OutboundSignature);
        Assert.DoesNotContain(563, result.OutboundSignature);
        Assert.DoesNotContain(565, result.OutboundSignature);
        Assert.InRange(result.CombinedWindow!.Start.TotalSeconds, 12.1, 12.3);
    }

    [Fact]
    public void Derives_the_measured_3074_signature()
    {
        var result = Calibrate(RealCaptureTimeline());

        Assert.Equal([51, 128, 136, 190, 198, 1166], result.OutboundSignature);
        Assert.True(result.SignatureIsUnambiguous);
        Assert.Empty(result.AmbiguousLengths);
    }

    [Fact]
    public void A_handshake_burst_inside_the_hold_does_not_split_the_window()
    {
        // Four packets landed in second 14 against a baseline of 10. Judging buckets individually
        // at the strict threshold would end the window there and lose most of the signature.
        var result = Calibrate(RealCaptureTimeline());

        Assert.Contains(51, result.OutboundSignature);    // before the burst
        Assert.Contains(1166, result.OutboundSignature);  // inside the burst
        Assert.Contains(198, result.OutboundSignature);   // after the burst
    }

    [Fact]
    public void The_collapse_is_reported_as_a_fraction_of_normal()
    {
        var result = Calibrate(RealCaptureTimeline());

        // 7 packets across 10 seconds against 10/s.
        Assert.InRange(result.Peers[0].CollapseRatio, 0.0, 0.1);
    }

    [Fact]
    public void Steady_traffic_yields_no_signature_at_all()
    {
        // The failure this design exists to prevent. An earlier calibrator differenced a timed
        // baseline against a timed hold; when the hold never engaged it still reported dozens of
        // "signature" sizes, because a short baseline cannot enumerate every size normal traffic
        // produces. Sizes here vary continuously and nothing is ever blocked.
        var packets = new List<PacketInfo>();
        var random = new Random(1234);

        for (var second = 0; second < 30; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                var at = second + (tick * 0.1);
                packets.Add(TestPackets.Outbound(3074, 440 + random.Next(0, 90) * 8, at));
                packets.Add(TestPackets.Inbound(3074, 1180 + random.Next(0, 80), at));
            }
        }

        var result = Calibrate(packets);

        Assert.False(result.BlockDetected);
        Assert.Empty(result.OutboundSignature);
    }

    [Fact]
    public void A_single_quiet_second_is_not_a_block()
    {
        var packets = new List<PacketInfo>();
        for (var second = 0; second < 20; second++)
        {
            if (second == 12)
            {
                continue;
            }

            for (var tick = 0; tick < 10; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500, second + (tick * 0.1)));
            }
        }

        Assert.False(Calibrate(packets).BlockDetected);
    }

    [Fact]
    public void A_quiet_start_is_not_mistaken_for_a_block()
    {
        // Buckets that establish the baseline are excluded from matching, so a run that begins
        // before traffic ramps up cannot report its own opening as a hold.
        var packets = new List<PacketInfo>();
        for (var second = 6; second < 20; second++)
        {
            for (var tick = 0; tick < 10; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500, second + (tick * 0.1)));
            }
        }

        Assert.False(Calibrate(packets).BlockDetected);
    }

    [Fact]
    public void Downlink_during_the_hold_is_captured_separately()
    {
        var result = Calibrate(RealCaptureTimeline());

        // Downlink was never blocked, so it keeps flowing through the window.
        Assert.True(result.PooledBlockInbound.Total > 90);
        Assert.Equal([1258], result.PooledBlockInbound.DistinctLengths);
    }

    [Fact]
    public void The_timeline_covers_every_second_and_marks_the_window()
    {
        var result = Calibrate(RealCaptureTimeline());

        Assert.Equal(26, result.Peers[0].Timeline.Count);
        Assert.Equal(Enumerable.Range(0, 26), result.Peers[0].Timeline.Select(b => b.Second));

        var marked = result.Peers[0].Timeline.Where(b => b.InWindow).Select(b => b.Second).ToArray();
        Assert.Equal(Enumerable.Range(9, 10), marked);

        // The window must be one contiguous run, or the bar chart misleads more than it explains.
        Assert.Equal(marked.Length, marked.Max() - marked.Min() + 1);
    }

    [Fact]
    public void A_rare_collision_with_normal_traffic_is_not_treated_as_serious()
    {
        // One stray match across hundreds of baseline packets is what a slightly narrow window
        // looks like, not evidence that the size is ordinary. Distinguishing the two is the
        // difference between "rerun this" and "this size is unusable".
        var packets = new List<PacketInfo>();

        for (var second = 0; second < 10; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                packets.Add(TestPackets.Outbound(3074, 500, second + (tick * 0.05)));
            }
        }

        // A single handshake-sized packet leaks into the baseline.
        packets.Add(TestPackets.Outbound(3074, 128, 9.9));

        for (var second = 10; second < 20; second++)
        {
            packets.Add(TestPackets.Outbound(3074, 128, second + 0.5));
        }

        var result = Calibrate(packets);

        Assert.True(result.BlockDetected);
        Assert.Contains(128, result.OutboundSignature);

        var ambiguous = Assert.Single(result.AmbiguousLengths);
        Assert.Equal(128, ambiguous.PayloadLength);
        Assert.Equal(1, ambiguous.BaselineCount);
        Assert.InRange(ambiguous.BaselineShare, 0, 0.01);

        Assert.Empty(result.SeriousConflicts());
    }

    [Fact]
    public void A_size_common_in_normal_traffic_is_excluded_from_the_signature()
    {
        // The signature is the sizes CONCENTRATED in the hold, not everything that crossed it.
        // A size occurring at a similar rate in both phases cannot be identified by length, and
        // passing it would pass ordinary game state - the one thing a selective block must not do.
        var packets = new List<PacketInfo>();

        for (var second = 0; second < 10; second++)
        {
            for (var tick = 0; tick < 20; tick++)
            {
                // Half of normal traffic is the same size the hold lets through.
                packets.Add(TestPackets.Outbound(3074, tick % 2 == 0 ? 500 : 128, second + (tick * 0.05)));
            }
        }

        for (var second = 10; second < 20; second++)
        {
            packets.Add(TestPackets.Outbound(3074, 128, second + 0.5));
        }

        var result = Calibrate(packets);

        Assert.True(result.BlockDetected);
        Assert.Contains(128, result.AllLengthsDuringHold);
        Assert.DoesNotContain(128, result.OutboundSignature);

        var excluded = Assert.Single(result.ExcludedAsOrdinary);
        Assert.Equal(128, excluded.PayloadLength);
        Assert.InRange(excluded.Enrichment, 0, CalibrationResult.MinimumEnrichment);
    }

    [Fact]
    public void A_hold_is_still_found_when_normal_traffic_is_bursty()
    {
        // Regression. Traffic on 3074 alternates between quiet and heavy rather than holding one
        // pace: a real 60 s run carried 531 packets - about 9/second - while more than half its
        // seconds sat at 3 or fewer. Taking the median of that lands in the quiet mode and sets a
        // collapse threshold *below* the ~1.7/second a genuine hold produces, so a real hold goes
        // undetected. An upper percentile describes the busy mode instead.
        var packets = new List<PacketInfo>();

        // Bursty means clustered, not thinly spread: packets arrive close together and the gap
        // falls between bursts. Spreading a quiet second's two packets evenly across it would make
        // that second indistinguishable from a second inside a hold, which is not how the traffic
        // behaves and would be testing a situation that does not arise.
        void Burst(int second, int count)
        {
            for (var i = 0; i < count; i++)
            {
                packets.Add(TestPackets.Outbound(3074, 500 + (i % 6 * 8), second + (i * 0.04)));
            }
        }

        for (var second = 0; second < 20; second++)
        {
            Burst(second, second % 2 == 0 ? 2 : 20);
        }

        // The hold: roughly one packet a second, which is above the median of the run above.
        double[] holdOffsets = [20.4, 21.6, 22.5, 23.8, 24.3, 25.7, 26.2, 27.9, 28.4, 29.1];
        int[] holdSizes = [128, 136, 51, 1166, 128, 1166, 51, 136, 128, 1166];
        for (var i = 0; i < holdOffsets.Length; i++)
        {
            packets.Add(TestPackets.Outbound(3074, holdSizes[i], holdOffsets[i]));
        }

        // Release: the buffer drains as a hard dump, measured at 59 packets inside one
        // millisecond. That spike is what ends the window.
        for (var i = 0; i < 40; i++)
        {
            packets.Add(TestPackets.Outbound(3074, 190 + (i % 5 * 8), 30.0 + (i * 0.0001)));
        }

        for (var second = 31; second < 40; second++)
        {
            Burst(second, second % 2 == 0 ? 2 : 20);
        }

        var result = Calibrate(packets);

        Assert.True(result.BlockDetected, "a hold at ~1/s must be found even when normal traffic is uneven");
        Assert.Equal([51, 128, 136, 1166], result.OutboundSignature);

        // The reported normal rate must describe the busy mode, not the average of both modes.
        Assert.True(
            result.Peers[0].BaselineOutboundRate >= 10,
            $"normal rate {result.Peers[0].BaselineOutboundRate:N1}/s should reflect active traffic, not quiet gaps");
    }

    [Fact]
    public void Packets_outside_the_group_are_ignored()
    {
        var calibrator = new SignatureCalibrator(DestinyPorts.Peer);

        calibrator.Observe(TestPackets.Outbound(3074, 500));
        calibrator.Observe(TestPackets.Outbound(27056, 500));
        calibrator.Observe(TestPackets.Outbound(443, 500));

        Assert.Equal(1, calibrator.ObservedCount);
    }

    [Fact]
    public void An_empty_run_reports_nothing_rather_than_throwing()
    {
        var result = Calibrate([]);

        Assert.False(result.BlockDetected);
        Assert.Empty(result.OutboundSignature);
        Assert.False(result.HasUsableBaseline);
    }

    [Fact]
    public void Two_of_HarryWatchs_five_hardcoded_constants_match_this_machine()
    {
        // Documents why signatures are configuration rather than constants. A reference tool
        // hardcodes 42/128/136/233/1258 as payload lengths; 128 and 136 appear here exactly,
        // the other three do not. Shipping its full set as immutable truth would misfire.
        int[] referenceConstants = [42, 128, 136, 233, 1258];
        var signature = Calibrate(RealCaptureTimeline()).OutboundSignature;

        Assert.Equal([128, 136], referenceConstants.Intersect(signature).Order());
    }

    [Fact]
    public void Largest_finds_the_trigger_length_for_a_weapon_swap()
    {
        // How TCP 7500 calibration works: perform the action, take the biggest packet it produced.
        var calibrator = new SignatureCalibrator(DestinyPorts.Api);
        var at = 0.0;

        foreach (var len in new[] { 40, 176, 184, 172, 40 })
        {
            calibrator.Observe(TestPackets.Inbound(7500, len, at += 0.05));
        }

        var histogram = new LengthHistogram();
        foreach (var len in new[] { 40, 176, 184, 172, 40 })
        {
            histogram.Add(len);
        }

        Assert.Equal(184, histogram.Largest);
    }
}
