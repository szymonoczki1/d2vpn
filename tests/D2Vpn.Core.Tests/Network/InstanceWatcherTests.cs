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
    public void A_reset_we_injected_does_not_restart_the_timer()
    {
        // The case that matters for this tool: reconnect makes the game reopen at once, and even
        // though it was observed returning to the same server there is no guarantee it always
        // does. Anything inside the grace window is ignored.
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Suppress(T0.AddSeconds(10));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(10.4)));

        Assert.Equal(T0, watcher.Since);
        Assert.Equal(ServerA, watcher.Server);
        Assert.Equal(1, watcher.Suppressed);
    }

    [Fact]
    public void A_real_change_after_the_grace_window_still_counts()
    {
        var watcher = new InstanceWatcher();
        watcher.Observe(Syn(ServerA, T0));

        watcher.Suppress(T0.AddSeconds(10));
        watcher.Observe(Syn(ServerB, T0.AddSeconds(20)));

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

    private static PacketInfo Syn(IPAddress server, DateTimeOffset at) =>
        Packet(server, at, TcpControlFlags.Syn);

    private static PacketInfo Data(IPAddress server, DateTimeOffset at) =>
        Packet(server, at, TcpControlFlags.Ack);

    private static PacketInfo Packet(IPAddress server, DateTimeOffset at, TcpControlFlags flags) =>
        new()
        {
            Timestamp = at,
            Direction = PacketDirection.Outbound,
            LocalPort = 50000,
            RemotePort = 30000,
            RemoteAddress = server,
            PayloadLength = flags == TcpControlFlags.Syn ? 0 : 64,
            InterfaceIndex = 1,
            TcpFlags = flags
        };
}
