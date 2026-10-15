using System.Net;

using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Network;

/// <summary>
/// The instance detector, against synthesised 30k traffic.
/// </summary>
/// <remarks>
/// The signal itself is a hypothesis with one observation behind it - see finding 6, and
/// <c>d2vpn instance</c> for settling it. What these cover is that the rule as written behaves the
/// way it is described, which is the part that can be decided without the game.
/// </remarks>
public class InstanceWatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly IPAddress ServerA = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress ServerB = IPAddress.Parse("192.0.2.20");

    [Fact]
    public void The_first_connection_starts_the_timer()
    {
        var watcher = new InstanceWatcher();
        InstanceChange? seen = null;
        watcher.Changed += c => seen = c;

        watcher.Observe(Syn(ServerA, T0));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(InstanceChangeReason.First, seen?.Reason);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_connection_to_a_new_server_restarts_it()
    {
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Syn(ServerB, T0.AddSeconds(40)));

        Assert.Equal(T0.AddSeconds(40), watcher.Since);
        Assert.Equal(ServerB, watcher.Server);
        Assert.Equal(2, watcher.Changes);
        Assert.Equal(TimeSpan.FromSeconds(5), watcher.Elapsed(T0.AddSeconds(45)));
    }

    [Fact]
    public void Reopening_to_the_same_server_is_not_a_change()
    {
        // What a reconnect looks like: finding 6 saw the game come back to the same server 330 ms
        // after the reset. The address is the discriminator, not the SYN.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Syn(ServerA, T0.AddMilliseconds(330)));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void Ordinary_traffic_to_a_new_server_is_not_a_change()
    {
        // Only a connection opening counts. A stray packet from somewhere else says nothing about
        // having moved instance.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Data(ServerB, T0.AddSeconds(10)));

        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_reconnect_that_lands_elsewhere_is_a_real_change()
    {
        // Deliberate, and the reason there is no grace window after our own reset. A reconnect
        // returns to the same server, which the test above already covers. If one ever does not,
        // the activity really has changed and the timer should restart - a window that swallowed
        // it would be suppressing the right answer.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Syn(ServerB, T0.AddSeconds(10.4)));

        Assert.Equal(T0.AddSeconds(10.4), watcher.Since);
        Assert.Equal(ServerB, watcher.Server);
        Assert.Equal(2, watcher.Changes);
    }

    [Fact]
    public void Returning_to_a_server_long_after_counts_as_a_change()
    {
        // Going back to an instance you were in an hour ago is still a change. The forget window
        // is what stops a brief stall looking like one.
        var watcher = new InstanceWatcher(TimeSpan.FromSeconds(20));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(30)));
        watcher.Observe(Syn(ServerA, T0.AddSeconds(90)));

        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(3, watcher.Changes);
    }

    [Fact]
    public void A_server_still_in_use_is_not_rediscovered()
    {
        // Several connections to one server - which 30k does - must not read as several changes.
        var watcher = new InstanceWatcher();

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Data(ServerA, T0.AddSeconds(5)));
        watcher.Observe(Syn(ServerA, T0.AddSeconds(10)));

        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_graceful_close_from_the_instance_server_ends_it()
    {
        // Measured: leaving an activity produced inbound [FIN, ACK] from the instance server on
        // port 30000, where a reset produces RST instead.
        var watcher = new InstanceWatcher();
        TimeSpan? lasted = null;
        watcher.Ended += (d, _) => lasted = d;

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(90)));

        Assert.False(watcher.InInstance);
        Assert.Null(watcher.Since);
        Assert.Equal(TimeSpan.FromSeconds(90), lasted);
        Assert.Equal(TimeSpan.FromSeconds(90), watcher.LastDuration);
        Assert.Equal(1, watcher.Ends);
    }

    [Fact]
    public void A_reset_does_not_end_the_instance()
    {
        // The case this whole distinction exists for: our own reconnect injects RST, and if that
        // read as leaving then every reconnect would stop the timer.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Packet(ServerA, T0.AddSeconds(30), TcpControlFlags.Reset,
            PacketDirection.Inbound));

        Assert.True(watcher.InInstance);
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void Closing_one_socket_of_several_does_not_end_it()
    {
        // The capture of leaving an activity showed two servers closing one connection each, so
        // the game holds more than one at a time. One FIN means a socket ended, not the instance -
        // and getting this wrong stops the timer mid-activity with no way back, since reopening to
        // a server already known does not restart it.
        var watcher = new InstanceWatcher();

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(1), 50002, TcpControlFlags.Syn));

        // Both carrying traffic up to the close. A connection silent for half a minute would have
        // been pruned, and rightly - this port carries a heartbeat every three seconds.
        for (var second = 3; second < 30; second += 3)
        {
            watcher.Observe(Port(ServerA, T0.AddSeconds(second), 50001, TcpControlFlags.Ack));
            watcher.Observe(Port(ServerA, T0.AddSeconds(second), 50002, TcpControlFlags.Ack));
        }

        watcher.Observe(Port(ServerA, T0.AddSeconds(30), 50001,
            TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound));

        Assert.True(watcher.InInstance);
        Assert.Equal(0, watcher.Ends);

        watcher.Observe(Port(ServerA, T0.AddSeconds(31), 50002,
            TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Ends);
    }

    [Fact]
    public void A_connection_open_before_watching_began_is_still_counted()
    {
        // Its SYN was never seen, so the live set has to be built from any packet rather than only
        // from connection setup - otherwise the first FIN would look like the last one.
        var watcher = new InstanceWatcher();

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(2), 50002, TcpControlFlags.Ack));
        watcher.Observe(Port(ServerA, T0.AddSeconds(14), 50002, TcpControlFlags.Ack));

        watcher.Observe(Port(ServerA, T0.AddSeconds(16), 50001,
            TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound));

        Assert.True(watcher.InInstance);
    }

    [Fact]
    public void Resetting_every_socket_does_not_read_as_a_close()
    {
        // What this tool's own reconnect does. A reset leaves its connection in the live set on
        // purpose, so emptying the set by resetting cannot be mistaken for the server closing.
        var watcher = new InstanceWatcher();

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(1), 50002, TcpControlFlags.Syn));

        watcher.Observe(Port(ServerA, T0.AddSeconds(10), 50001, TcpControlFlags.Reset,
            PacketDirection.Inbound));
        watcher.Observe(Port(ServerA, T0.AddSeconds(10), 50002, TcpControlFlags.Reset,
            PacketDirection.Inbound));

        Assert.True(watcher.InInstance);
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void A_reset_connection_does_not_block_a_later_departure()
    {
        // The bug the connection tracking introduced. A reset leaves no FIN, so without pruning its
        // port stayed in the live set for ever, the set never emptied, and every leave after one
        // reconnect fell through to the thirty-second silence instead of being noticed at once.
        var watcher = new InstanceWatcher(portIdle: TimeSpan.FromSeconds(10));

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(5), 50001, TcpControlFlags.Reset,
            PacketDirection.Inbound));

        // The game reopens on a new port and carries on; the reset one is never heard from again.
        watcher.Observe(Port(ServerA, T0.AddSeconds(6), 50002, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(20), 50002, TcpControlFlags.Ack));

        watcher.Observe(Port(ServerA, T0.AddSeconds(25), 50002,
            TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Ends);
    }

    [Fact]
    public void A_connection_merely_between_packets_is_not_pruned()
    {
        // Pruning must not mistake a quiet moment for a dead socket, or closing the other one would
        // end the instance early.
        var watcher = new InstanceWatcher(portIdle: TimeSpan.FromSeconds(10));

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(1), 50002, TcpControlFlags.Syn));

        // Both still carrying traffic, a few seconds apart as the heartbeat does.
        watcher.Observe(Port(ServerA, T0.AddSeconds(6), 50001, TcpControlFlags.Ack));
        watcher.Observe(Port(ServerA, T0.AddSeconds(7), 50002, TcpControlFlags.Ack));

        watcher.Observe(Port(ServerA, T0.AddSeconds(8), 50002,
            TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound));

        Assert.True(watcher.InInstance);
    }

    private static PacketInfo Port(
        IPAddress server,
        DateTimeOffset at,
        int localPort,
        TcpControlFlags flags,
        PacketDirection direction = PacketDirection.Outbound) =>
        new()
        {
            Timestamp = at,
            Direction = direction,
            LocalPort = localPort,
            RemotePort = 30000,
            RemoteAddress = server,
            PayloadLength = 0,
            InterfaceIndex = 1,
            TcpFlags = flags
        };

    [Fact]
    public void A_close_from_some_other_server_is_ignored()
    {
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Fin(ServerB, T0.AddSeconds(20)));

        Assert.True(watcher.InInstance);
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void An_outbound_close_is_not_the_server_closing()
    {
        // The client finishing its side says nothing on its own; the instance ending is the server
        // closing on us.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Observe(Packet(ServerA, T0.AddSeconds(20), TcpControlFlags.Fin,
            PacketDirection.Outbound));

        Assert.True(watcher.InInstance);
    }

    [Fact]
    public void Entering_again_after_leaving_restarts_the_timer()
    {
        var watcher = new InstanceWatcher();

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(60)));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(75)));

        Assert.True(watcher.InInstance);
        Assert.Equal(T0.AddSeconds(75), watcher.Since);
        Assert.Equal(TimeSpan.FromSeconds(60), watcher.LastDuration);
    }

    [Fact]
    public void Silence_ends_the_instance_when_nothing_closed_it()
    {
        // The backstop for an error code, which drops the connection without the server closing it
        // politely - so no FIN arrives and the timer would otherwise count on for ever. Thirty
        // seconds is ten of the 3.02s heartbeats this port carries.
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));
        InstanceEndReason? reason = null;
        watcher.Ended += (_, r) => reason = r;

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Data(ServerA, T0.AddSeconds(10)));

        Assert.False(watcher.Tick(T0.AddSeconds(39)));
        Assert.True(watcher.InInstance);

        Assert.True(watcher.Tick(T0.AddSeconds(41)));
        Assert.False(watcher.InInstance);
        Assert.Equal(InstanceEndReason.WentQuiet, reason);
        Assert.Equal(1, watcher.QuietEnds);
    }

    [Fact]
    public void A_quiet_ending_is_dated_from_the_last_packet()
    {
        // The instance stopped when the traffic did, not when something got round to noticing.
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Data(ServerA, T0.AddSeconds(10)));
        watcher.Tick(T0.AddSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(10), watcher.LastDuration);
    }

    [Fact]
    public void Traffic_keeps_the_instance_alive_indefinitely()
    {
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));
        watcher.Observe(Syn(ServerA, T0));

        for (var second = 3; second <= 300; second += 3)
        {
            watcher.Observe(Data(ServerA, T0.AddSeconds(second)));
            Assert.False(watcher.Tick(T0.AddSeconds(second)));
        }

        Assert.True(watcher.InInstance);
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void Silence_does_not_end_an_instance_that_already_ended()
    {
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(20)));

        Assert.False(watcher.Tick(T0.AddSeconds(120)));
        Assert.Equal(1, watcher.Ends);
        Assert.Equal(0, watcher.QuietEnds);
    }

    [Fact]
    public void Silence_before_anything_was_seen_ends_nothing()
    {
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));

        Assert.False(watcher.Tick(T0.AddSeconds(600)));
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void Ordinary_traffic_during_an_instance_still_changes_nothing()
    {
        // The resume rule is limited to not being in one, or every packet would restart the timer.
        var watcher = new InstanceWatcher();

        watcher.Observe(Syn(ServerA, T0));

        for (var second = 3; second <= 30; second += 3)
        {
            watcher.Observe(Data(ServerA, T0.AddSeconds(second)));
        }

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_quiet_instance_is_hidden_before_it_is_ended()
    {
        // The loading-screen case: traffic stops, so the timer goes out of sight, but the instance
        // has not ended and the figure keeps counting behind it.
        var watcher = new InstanceWatcher(
            dormantAfter: TimeSpan.FromSeconds(15), silence: TimeSpan.FromSeconds(45));

        watcher.Observe(Syn(ServerA, T0));

        Assert.True(watcher.ShouldShow(T0.AddSeconds(10)));
        Assert.False(watcher.IsDormant(T0.AddSeconds(10)));

        Assert.False(watcher.ShouldShow(T0.AddSeconds(20)));
        Assert.True(watcher.IsDormant(T0.AddSeconds(20)));
        Assert.True(watcher.InInstance);

        // Still running, so the figure is still climbing while nothing is shown.
        Assert.Equal(TimeSpan.FromSeconds(20), watcher.Elapsed(T0.AddSeconds(20)));

        Assert.False(watcher.Tick(T0.AddSeconds(44)));
        Assert.True(watcher.Tick(T0.AddSeconds(46)));
        Assert.False(watcher.InInstance);
    }

    [Fact]
    public void Returning_to_the_same_server_while_dormant_does_not_reset()
    {
        // The requirement this whole state exists for. A load screen that runs past the hide wait
        // must not cost the elapsed time when the same instance comes back.
        var watcher = new InstanceWatcher(
            dormantAfter: TimeSpan.FromSeconds(15), silence: TimeSpan.FromSeconds(45));

        watcher.Observe(Syn(ServerA, T0));
        Assert.True(watcher.IsDormant(T0.AddSeconds(30)));

        watcher.Observe(Data(ServerA, T0.AddSeconds(35)));

        Assert.Equal(T0, watcher.Since);
        Assert.True(watcher.ShouldShow(T0.AddSeconds(35)));
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_reopened_connection_to_the_same_server_while_dormant_does_not_reset()
    {
        // The forget window has to outlast the silence wait, or a server dropped from the known set
        // reads as new and restarts the very instance the wait exists to preserve - and only when a
        // load screen happens to run long, which is the worst way to find a bug.
        var watcher = new InstanceWatcher(
            forget: TimeSpan.FromSeconds(20),
            dormantAfter: TimeSpan.FromSeconds(15),
            silence: TimeSpan.FromSeconds(45));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Syn(ServerA, T0.AddSeconds(40)));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void Moving_to_another_server_while_dormant_closes_the_old_one_at_its_last_packet()
    {
        var watcher = new InstanceWatcher(
            dormantAfter: TimeSpan.FromSeconds(15), silence: TimeSpan.FromSeconds(45));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Data(ServerA, T0.AddSeconds(20)));

        watcher.Observe(Syn(ServerB, T0.AddSeconds(40)));

        // The old instance stopped when its traffic did, not when the new one started.
        Assert.Equal(TimeSpan.FromSeconds(20), watcher.LastDuration);
        Assert.Equal(T0.AddSeconds(40), watcher.Since);
        Assert.Equal(ServerB, watcher.Server);
    }

    [Fact]
    public void The_countdown_to_ending_is_reported()
    {
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(45));
        watcher.Observe(Syn(ServerA, T0));

        Assert.Equal(TimeSpan.FromSeconds(25), watcher.UntilEnd(T0.AddSeconds(20)));
        Assert.Equal(TimeSpan.Zero, watcher.UntilEnd(T0.AddSeconds(90)));
    }

    [Fact]
    public void Traffic_alone_never_starts_an_instance()
    {
        // There was a rule that it did, taken from a reference implementation, and it was wrong
        // on this port
        // twice: it restarted an instance from its own teardown, and then manufactured one out of
        // every burst of 30k chatter in orbit. 3074 is peer traffic and genuinely silent outside an
        // activity; 30k talks to servers in menus, so traffic arriving says nothing on its own.
        var watcher = new InstanceWatcher(dormantAfter: TimeSpan.FromSeconds(15));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(60)));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Changes);

        // Chatter long afterwards, on connections that were never re-opened. No SYN, no instance.
        watcher.Observe(Data(ServerA, T0.AddSeconds(200)));
        watcher.Observe(Data(ServerB, T0.AddSeconds(260)));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_connection_opening_after_a_close_does_start_one()
    {
        // What entering actually looks like, and the only thing that counts.
        var watcher = new InstanceWatcher(dormantAfter: TimeSpan.FromSeconds(15));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(60)));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(200)));

        Assert.True(watcher.InInstance);
        Assert.Equal(ServerB, watcher.Server);
        Assert.Equal(2, watcher.Changes);
    }

    [Fact]
    public void Returning_to_a_recently_used_server_still_counts_as_entering()
    {
        // The case requiring a new server missed. Coming back to a server still inside the forget
        // window - orbit, most obviously - opens a connection to one already known, and reading
        // that as nothing happening lost two real instances in a measured run.
        var watcher = new InstanceWatcher(forget: TimeSpan.FromMinutes(5));
        InstanceChangeReason? reason = null;
        watcher.Changed += c => reason = c.Reason;

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(30)));

        Assert.False(watcher.InInstance);

        // Back to the same server, well inside the forget window.
        watcher.Observe(Syn(ServerA, T0.AddSeconds(60)));

        Assert.True(watcher.InInstance);
        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(InstanceChangeReason.Entered, reason);
        Assert.Equal(T0.AddSeconds(60), watcher.Since);
    }

    [Fact]
    public void A_second_socket_to_the_current_server_is_not_a_change()
    {
        // The reason the known check is kept at all, now that it only applies while in one.
        var watcher = new InstanceWatcher();

        watcher.Observe(Port(ServerA, T0, 50001, TcpControlFlags.Syn));
        watcher.Observe(Port(ServerA, T0.AddSeconds(5), 50002, TcpControlFlags.Syn));

        Assert.Equal(1, watcher.Changes);
        Assert.Equal(T0, watcher.Since);
    }

    [Fact]
    public void The_tail_of_a_close_does_not_start_a_new_instance()
    {
        // Reported from a run: leaving an activity ended the instance and immediately began
        // another. Closing a TCP connection takes more than the FIN - the acknowledgements
        // finishing the teardown arrive milliseconds later - and resuming on any packet rather
        // than on a packet after a gap read those as traffic returning.
        var watcher = new InstanceWatcher(dormantAfter: TimeSpan.FromSeconds(15));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(60)));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Ends);

        // The teardown finishing, a fraction of a second later.
        watcher.Observe(Data(ServerA, T0.AddSeconds(60.1)));
        watcher.Observe(Data(ServerA, T0.AddSeconds(60.3)));

        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_new_server_during_a_teardown_is_still_a_new_instance()
    {
        // Falling through rather than returning: the tail of one close must not swallow a genuine
        // connection somewhere else.
        var watcher = new InstanceWatcher(dormantAfter: TimeSpan.FromSeconds(15));

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Fin(ServerA, T0.AddSeconds(60)));
        watcher.Observe(Data(ServerA, T0.AddSeconds(60.1)));

        watcher.Observe(Syn(ServerB, T0.AddSeconds(60.5)));

        Assert.True(watcher.InInstance);
        Assert.Equal(ServerB, watcher.Server);
    }

    [Fact]
    public void An_instance_replaced_by_another_is_superseded_not_quiet()
    {
        // Travelling straight from one activity to another. It was counted as having gone quiet,
        // which is where the traffic stopped but not why the instance ended - and it put endings
        // in the gone-quiet tally that were nothing of the sort.
        var watcher = new InstanceWatcher();
        InstanceEndReason? reason = null;
        watcher.Ended += (_, r) => reason = r;

        watcher.Observe(Syn(ServerA, T0));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(40)));

        Assert.Equal(InstanceEndReason.Superseded, reason);
        Assert.Equal(1, watcher.Superseded);
        Assert.Equal(0, watcher.QuietEnds);
        Assert.Equal(1, watcher.Ends);
    }

    [Fact]
    public void Retuning_does_not_disturb_the_instance_being_timed()
    {
        // Because a running activity is exactly when someone would go and adjust these. Rebuilding
        // the watcher would be simpler and would reset the timer the moment the number moved.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Retune(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_retuned_wait_is_the_one_that_applies()
    {
        var watcher = new InstanceWatcher(silence: TimeSpan.FromSeconds(30));
        watcher.Observe(Syn(ServerA, T0));

        watcher.Retune(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30));

        // Past the old wait, inside the new one.
        watcher.Tick(T0.AddSeconds(45));
        Assert.True(watcher.InInstance);

        watcher.Tick(T0.AddSeconds(95));
        Assert.False(watcher.InInstance);
        Assert.Equal(1, watcher.QuietEnds);
    }

    [Fact]
    public void A_long_wait_cannot_be_undone_by_the_forget_window()
    {
        // The invariant the constructor enforces, which retuning has to keep. A server dropped
        // from the known set reads as new when it is next heard from, which restarts the timer -
        // so a forget shorter than the silence wait would reset the very instance that wait exists
        // to preserve, and only when a load screen happened to run long.
        var watcher = new InstanceWatcher(forget: TimeSpan.FromSeconds(20));
        watcher.Observe(Syn(ServerA, T0));

        watcher.Retune(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15));

        // A hundred seconds of nothing, then the same server again. Still the same instance.
        watcher.Tick(T0.AddSeconds(100));
        watcher.Observe(Syn(ServerA, T0.AddSeconds(100)));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_retuned_hide_wait_applies_too()
    {
        var watcher = new InstanceWatcher(dormantAfter: TimeSpan.FromSeconds(15));
        watcher.Observe(Syn(ServerA, T0));

        watcher.Retune(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(40));

        // Past the old hide wait, inside the new one: still shown, and still running either way.
        Assert.True(watcher.ShouldShow(T0.AddSeconds(30)));
        Assert.False(watcher.ShouldShow(T0.AddSeconds(50)));
        Assert.True(watcher.InInstance);
    }

    [Fact]
    public void Clearing_forgets_the_instance_and_the_servers()
    {
        // What turning the timer off has to do. Left alone it would keep showing the instance it
        // was in when it stopped.
        var watcher = new InstanceWatcher();
        var ended = 0;
        watcher.Ended += (_, _) => ended++;

        watcher.Observe(Syn(ServerA, T0));
        watcher.Clear();

        Assert.False(watcher.InInstance);
        Assert.Null(watcher.Server);
        Assert.Equal(0, watcher.Changes);

        // Not an ending. Nothing finished - it stopped being followed.
        Assert.Equal(0, ended);
        Assert.Equal(0, watcher.Ends);
    }

    [Fact]
    public void Watching_again_after_a_clear_starts_from_the_next_connection()
    {
        // The stale server set is the thing to avoid: switching back on hours later and finding
        // the old server still "known" would read as having never left it.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));
        watcher.Clear();

        watcher.Observe(Syn(ServerA, T0.AddSeconds(5)));

        Assert.Equal(T0.AddSeconds(5), watcher.Since);
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void The_api_start_replaces_the_traffic_one()
    {
        // The whole point of the API half. Traffic dates the instance from the connection, which
        // is after the activity began; the server knows when it began and nothing else does.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0.AddSeconds(4)));

        Assert.True(watcher.Correct(T0));

        Assert.Equal(T0, watcher.Since);
        Assert.True(watcher.IsCorrected);
        Assert.Equal(1, watcher.Corrections);

        // Not a change. The instance is the same one; only its start moved.
        Assert.Equal(1, watcher.Changes);
    }

    [Fact]
    public void A_stale_api_start_is_refused()
    {
        // The API's current activity lags: for the first seconds after entering somewhere it still
        // describes the last place. Taking that would drag the timer back into an activity that is
        // already over, which is worse than not correcting at all.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        Assert.False(watcher.Correct(T0.AddMinutes(-20)));

        Assert.Equal(T0, watcher.Since);
        Assert.False(watcher.IsCorrected);
    }

    [Fact]
    public void There_is_nothing_to_correct_outside_an_instance()
    {
        var watcher = new InstanceWatcher();

        Assert.False(watcher.Correct(T0));
        Assert.False(watcher.IsCorrected);
    }

    [Fact]
    public void The_same_correction_is_only_applied_once()
    {
        // It is returned for as long as the activity runs. Applying it again is harmless, but
        // saying so is what lets the poller stop asking.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0.AddSeconds(4)));

        Assert.True(watcher.Correct(T0));
        Assert.False(watcher.Correct(T0));
        Assert.Equal(1, watcher.Corrections);
    }

    [Fact]
    public void A_new_instance_is_not_corrected_until_it_is()
    {
        // Whatever the API said was about the instance that just ended.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0.AddSeconds(4)));
        watcher.Correct(T0);

        watcher.Observe(Syn(ServerB, T0.AddSeconds(90)));

        Assert.False(watcher.IsCorrected);
        Assert.Equal(T0.AddSeconds(90), watcher.Since);
    }

    private static PacketInfo Fin(IPAddress server, DateTimeOffset at) =>
        Packet(server, at, TcpControlFlags.Fin | TcpControlFlags.Ack, PacketDirection.Inbound);

    private static PacketInfo Syn(IPAddress server, DateTimeOffset at) =>
        Packet(server, at, TcpControlFlags.Syn);

    private static PacketInfo Data(IPAddress server, DateTimeOffset at) =>
        Packet(server, at, TcpControlFlags.Ack);

    private static PacketInfo Packet(
        IPAddress server,
        DateTimeOffset at,
        TcpControlFlags flags,
        PacketDirection direction = PacketDirection.Outbound) =>
        new()
        {
            Timestamp = at,
            Direction = direction,
            LocalPort = 50000,
            RemotePort = 30000,
            RemoteAddress = server,
            PayloadLength = flags == TcpControlFlags.Syn ? 0 : 64,
            InterfaceIndex = 1,
            TcpFlags = flags
        };
}
