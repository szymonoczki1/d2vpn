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

    private static void Hold(PacketBuffer buffer, DateTimeOffset at)
    {
        using var packet = new WinDivertPacket();
        using var address = new WinDivertAddress();
        buffer.Hold(packet, address, at);
    }

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

    [Fact]
    public void Nothing_is_due_before_the_delay_has_elapsed()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);

        Assert.Empty(buffer.TakeDue(T0.AddSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void A_packet_is_due_once_it_has_been_held_for_the_delay()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);

        var due = buffer.TakeDue(T0.AddSeconds(2), TimeSpan.FromSeconds(2));

        Assert.Single(due);
        Assert.Equal(0, buffer.Count);
        due[0].Dispose();
    }

    [Fact]
    public void Only_the_due_prefix_is_taken_and_the_rest_stays_queued()
    {
        // The delay line has to keep the stream in order. Delivering it late is the point;
        // delivering it shuffled is just corruption.
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++)
        {
            Hold(buffer, T0.AddSeconds(i));
        }

        // At t=6 with a 2s delay, those held at 0..4 are due.
        var due = buffer.TakeDue(T0.AddSeconds(6), TimeSpan.FromSeconds(2));

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
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);                   // due at t=3 with a 2s delay
        Hold(buffer, T0.AddSeconds(1));     // due
        Hold(buffer, T0.AddSeconds(2.5));   // not yet

        var due = buffer.TakeDue(T0.AddSeconds(3), TimeSpan.FromSeconds(2));

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
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(10));
        Hold(buffer, T0);

        Assert.Empty(buffer.TakeDue(T0.AddSeconds(40), TimeSpan.FromSeconds(2)));
        Assert.Equal(1, buffer.Expired);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Taking_due_packets_counts_them_as_replayed()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);
        Hold(buffer, T0);

        foreach (var entry in buffer.TakeDue(T0.AddSeconds(5), TimeSpan.FromSeconds(2)))
        {
            entry.Dispose();
        }

        Assert.Equal(2, buffer.Replayed);
    }

    [Fact]
    public void A_zero_delay_makes_everything_immediately_due()
    {
        using var buffer = new PacketBuffer(16, TimeSpan.FromSeconds(30));
        Hold(buffer, T0);

        var due = buffer.TakeDue(T0, TimeSpan.Zero);

        Assert.Single(due);
        due[0].Dispose();
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
