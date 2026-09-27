using System.Net;
using D2Vpn.Core.Interception;
using WindivertDotnet;

namespace D2Vpn.Core.Tests.Interception;

/// <summary>
/// Every entry owns unmanaged memory, so this is really about lifetime: a held packet leaves
/// exactly once, whether replayed, evicted, expired or disposed with the buffer. Leaking here
/// leaks for the length of a play session.
/// </summary>
public class PacketBufferTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static void Hold(PacketBuffer buffer, DateTimeOffset at, IPAddress? peer = null)
    {
        using var packet = new WinDivertPacket();
        using var address = new WinDivertAddress();
        buffer.Hold(packet, address, at, peer);
    }

    /// <summary>A buffer whose packets leave a fixed time after their own arrival.</summary>
    private static PacketBuffer Fixed(TimeSpan delay, TimeSpan? maxAge = null) =>
        new(16, maxAge ?? TimeSpan.FromSeconds(30), new PacketBuffer.SlowSchedule(delay));

    /// <summary>A buffer whose packets leave a fixed time after the previous one per peer.</summary>
    private static PacketBuffer Throttled(
        TimeSpan delay, TimeSpan? burst = null, int capacity = 64) =>
        new(capacity, TimeSpan.FromMinutes(10),
            new PacketBuffer.SlowSchedule(
                delay, Throttle: true, BurstWindow: burst ?? TimeSpan.FromMilliseconds(10)));

    [Fact]
    public void Held_packets_come_back_in_the_order_they_arrived()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0.AddSeconds(i));
        }

        var replay = buffer.TakeForReplay(T0.AddSeconds(5));

        Assert.Equal(5, replay.Count);
        Assert.Equal(replay.OrderBy(e => e.HeldAt).Select(e => e.HeldAt), replay.Select(e => e.HeldAt));

        foreach (var entry in replay)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Taking_for_replay_empties_the_buffer()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);

        foreach (var entry in buffer.TakeForReplay(T0))
        {
            entry.Dispose();
        }

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void The_oldest_are_evicted_when_full()
    {
        // What matters on release is the most recent actions - the shots just taken - so the
        // entries whose moment has passed are the ones to lose.
        using var buffer = new PacketBuffer(3, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0.AddSeconds(i));
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.Evicted);

        var replay = buffer.TakeForReplay(T0.AddSeconds(5));
        Assert.Equal([T0.AddSeconds(2), T0.AddSeconds(3), T0.AddSeconds(4)],
            replay.Select(e => e.HeldAt));

        foreach (var entry in replay)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Stale_packets_are_discarded_rather_than_replayed()
    {
        // A position update thirty seconds old is worse than useless on arrival.
        using var buffer = new PacketBuffer(64, TimeSpan.FromSeconds(10));

        Hold(buffer, T0);                    // 40s old at replay
        Hold(buffer, T0.AddSeconds(35));     // 5s old
        Hold(buffer, T0.AddSeconds(38));     // 2s old

        var replay = buffer.TakeForReplay(T0.AddSeconds(40));

        Assert.Equal(2, replay.Count);
        Assert.Equal(1, buffer.Expired);
        Assert.DoesNotContain(T0, replay.Select(e => e.HeldAt));

        foreach (var entry in replay)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Counters_track_what_happened_to_each_packet()
    {
        using var buffer = new PacketBuffer(2, TimeSpan.FromSeconds(10));

        Hold(buffer, T0);
        Hold(buffer, T0.AddSeconds(1));
        Hold(buffer, T0.AddSeconds(2));       // evicts the first

        foreach (var entry in buffer.TakeForReplay(T0.AddSeconds(3)))
        {
            entry.Dispose();
        }

        Assert.Equal(1, buffer.Evicted);
        Assert.Equal(2, buffer.Replayed);
        Assert.Equal(0, buffer.Expired);
    }

    // ---- fixed schedule: each packet leaves the delay after its own arrival ----

    [Fact]
    public void Nothing_is_due_before_the_delay_has_elapsed()
    {
        using var buffer = Fixed(TimeSpan.FromSeconds(2));
        Hold(buffer, T0);

        Assert.Empty(buffer.TakeDue(T0.AddSeconds(1)));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Without_a_schedule_nothing_ever_comes_due()
    {
        // A plain hold releases only on untick, however long it runs.
        using var buffer = new PacketBuffer(16, TimeSpan.FromDays(2));
        Hold(buffer, T0);

        Assert.Empty(buffer.TakeDue(T0.AddDays(1)));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void A_fixed_schedule_preserves_the_stream_shape()
    {
        // The defining property: output spacing equals input spacing, the whole stream simply
        // shifted. That is what makes it read as latency rather than as starvation.
        using var buffer = Fixed(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0.AddSeconds(i));
        }

        var due = buffer.TakeDue(T0.AddSeconds(6));

        Assert.Equal(5, due.Count);
        Assert.Equal(
            due.Select(e => e.HeldAt + TimeSpan.FromSeconds(2)),
            due.Select(e => e.DueAt));

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_packet_is_due_once_it_has_been_held_for_the_delay()
    {
        using var buffer = Fixed(TimeSpan.FromSeconds(2));
        Hold(buffer, T0);

        var due = buffer.TakeDue(T0.AddSeconds(2));

        Assert.Single(due);
        Assert.Equal(0, buffer.Count);
        due[0].Dispose();
    }

    [Fact]
    public void Only_the_due_prefix_is_taken_and_the_rest_stays_queued()
    {
        // The delay line has to keep the stream in order. Delivering it late is the point;
        // delivering it shuffled is just corruption.
        using var buffer = Fixed(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0.AddSeconds(i));
        }

        // At t=6 with a 2s delay, those held at 0..4 are due.
        var due = buffer.TakeDue(T0.AddSeconds(6));

        Assert.Equal(5, due.Count);
        Assert.Equal(due.OrderBy(e => e.HeldAt).Select(e => e.HeldAt), due.Select(e => e.HeldAt));

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_partly_due_queue_delivers_the_old_and_keeps_the_new()
    {
        using var buffer = Fixed(TimeSpan.FromSeconds(2));
        Hold(buffer, T0);                   // due at t=3 with a 2s delay
        Hold(buffer, T0.AddSeconds(1));     // due
        Hold(buffer, T0.AddSeconds(2.5));   // not yet

        var due = buffer.TakeDue(T0.AddSeconds(3));

        Assert.Equal(2, due.Count);
        Assert.Equal(1, buffer.Count);

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_packet_that_went_stale_while_queued_is_not_delivered_late()
    {
        // Delivery is driven by packet arrivals, so a port that goes quiet can leave entries
        // queued past the point of being worth sending. Age still wins over due-ness.
        using var buffer = Fixed(TimeSpan.FromSeconds(2), maxAge: TimeSpan.FromSeconds(10));
        Hold(buffer, T0);

        Assert.Empty(buffer.TakeDue(T0.AddSeconds(40)));
        Assert.Equal(1, buffer.Expired);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Taking_due_packets_counts_them_as_replayed()
    {
        using var buffer = Fixed(TimeSpan.FromSeconds(2));
        Hold(buffer, T0);
        Hold(buffer, T0);

        foreach (var entry in buffer.TakeDue(T0.AddSeconds(5)))
        {
            entry.Dispose();
        }

        Assert.Equal(2, buffer.Replayed);
    }

    [Fact]
    public void A_zero_delay_makes_everything_immediately_due()
    {
        using var buffer = Fixed(TimeSpan.Zero);
        Hold(buffer, T0);

        var due = buffer.TakeDue(T0);

        Assert.Single(due);
        due[0].Dispose();
    }

    // ---- throttled schedule: each packet leaves the delay after the previous one, per peer ----

    [Fact]
    public void A_throttle_spaces_output_by_the_delay_whatever_the_input_rate()
    {
        // Ten packets arriving inside a second leave one second apart. This is the difference in
        // kind from a fixed offset: the stream is reshaped, not shifted.
        using var buffer = Throttled(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 10; i++)
        {
            Hold(buffer, T0.AddSeconds(i * 0.1));   // 100 ms apart, outside the 10 ms burst window
        }

        // Scheduled at 1s, 2s, ... 10s, so by t=3.5 only three have come due.
        var due = buffer.TakeDue(T0.AddSeconds(3.5));

        Assert.Equal(3, due.Count);
        Assert.Equal(7, buffer.Count);

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_throttled_backlog_grows_while_input_outruns_output()
    {
        // The property that makes a throttle compound where an offset does not: held long enough,
        // the effective delay climbs without bound.
        using var buffer = Throttled(TimeSpan.FromSeconds(1), capacity: 512);

        for (var i = 0; i < 100; i++)
        {
            Hold(buffer, T0.AddSeconds(i * 0.1));   // 100 packets over 10 s
        }

        var due = buffer.TakeDue(T0.AddSeconds(10));

        Assert.InRange(due.Count, 9, 11);           // ~10 released at a 1/s cap
        Assert.InRange(buffer.Count, 89, 91);       // the rest still waiting

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_burst_stays_a_burst_rather_than_being_smeared()
    {
        // Without the window, four packets 2 ms apart would leave four seconds apart, which
        // throttles far harder than 1/s suggests and destroys whatever the burst carries.
        using var buffer = Throttled(TimeSpan.FromSeconds(1), burst: TimeSpan.FromMilliseconds(10));

        Hold(buffer, T0);
        Hold(buffer, T0.AddMilliseconds(2));
        Hold(buffer, T0.AddMilliseconds(4));
        Hold(buffer, T0.AddMilliseconds(6));

        var due = buffer.TakeDue(T0.AddSeconds(2));

        Assert.Equal(4, due.Count);

        var gaps = due.Zip(due.Skip(1), (a, b) => b.DueAt - a.DueAt);
        Assert.All(gaps, gap => Assert.Equal(TimeSpan.FromMilliseconds(10), gap));

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_throttle_paces_each_peer_independently()
    {
        // Per peer, as the reference limiter does. Two peers at a 1 s cap deliver two packets in
        // the first second between them - a globally keyed throttle would be twice as severe.
        using var buffer = Throttled(TimeSpan.FromSeconds(1));

        Hold(buffer, T0, IPAddress.Parse("192.0.2.10"));
        Hold(buffer, T0, IPAddress.Parse("192.0.2.11"));

        var due = buffer.TakeDue(T0.AddSeconds(1));

        Assert.Equal(2, due.Count);

        foreach (var entry in due)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void A_drained_peer_starts_its_chain_again_from_arrival()
    {
        // Otherwise a peer stays punished for a backlog it no longer has, the chain accumulating
        // from a release time long past.
        using var buffer = Throttled(TimeSpan.FromSeconds(1));
        var peer = IPAddress.Parse("192.0.2.10");

        Hold(buffer, T0, peer);
        foreach (var entry in buffer.TakeDue(T0.AddSeconds(1)))
        {
            entry.Dispose();
        }

        Hold(buffer, T0.AddSeconds(60), peer);
        var due = buffer.TakeDue(T0.AddSeconds(61));

        Assert.Single(due);
        Assert.Equal(T0.AddSeconds(61), due[0].DueAt);
        due[0].Dispose();
    }

    [Fact]
    public void A_release_clears_the_throttle_chain()
    {
        // Untick dumps the queue, so the next hold must not schedule against the old backlog.
        using var buffer = Throttled(TimeSpan.FromSeconds(1));
        var peer = IPAddress.Parse("192.0.2.10");

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0, peer);
        }

        foreach (var entry in buffer.TakeForReplay(T0))
        {
            entry.Dispose();
        }

        Hold(buffer, T0.AddSeconds(1), peer);
        var due = buffer.TakeDue(T0.AddSeconds(2));

        Assert.Single(due);
        Assert.Equal(T0.AddSeconds(2), due[0].DueAt);
        due[0].Dispose();
    }

    [Fact]
    public void A_release_dumps_the_throttled_backlog_due_or_not()
    {
        // The specified behaviour: on untick the whole buffer goes, however far behind schedule.
        using var buffer = Throttled(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 20; i++)
        {
            Hold(buffer, T0.AddSeconds(i * 0.1));
        }

        var replay = buffer.TakeForReplay(T0.AddSeconds(2));

        Assert.Equal(20, replay.Count);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Expired);

        foreach (var entry in replay)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Discarding_empties_the_buffer_without_replaying()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);
        Hold(buffer, T0);

        buffer.DiscardAll();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Replayed);
    }

    [Fact]
    public void Holding_after_disposal_is_ignored_rather_than_throwing()
    {
        // Teardown races the receive loop, and a packet arriving during it must not take the
        // process down or resurrect a disposed queue.
        var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        buffer.Dispose();

        Hold(buffer, T0);

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);

        buffer.Dispose();
        buffer.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_capacity_below_one_is_rejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PacketBuffer(capacity, TimeSpan.FromSeconds(30)));

    [Fact]
    public void A_non_positive_max_age_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketBuffer(16, TimeSpan.Zero));

    [Fact]
    public async Task Holding_and_draining_concurrently_does_not_corrupt_the_queue()
    {
        // The receive loop holds packets while a release drains, so both happen at once.
        using var buffer = new PacketBuffer(256, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var holding = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                Hold(buffer, DateTimeOffset.UtcNow);
            }
        });

        var draining = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var entry in buffer.TakeForReplay(DateTimeOffset.UtcNow))
                {
                    entry.Dispose();
                }

                Thread.Sleep(1);
            }
        });

        await Task.WhenAll(holding, draining).WaitAsync(TimeSpan.FromSeconds(15));
    }
}
