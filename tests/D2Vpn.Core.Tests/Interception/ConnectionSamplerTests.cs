using System.Net;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;
using WindivertDotnet;

namespace D2Vpn.Core.Tests.Interception;

/// <summary>
/// A reset cannot be conjured from nothing - to be accepted it must carry a real conversation's
/// addresses, ports and sequence numbers - so it is built from a packet that connection recently
/// sent. These cover keeping the right sample and turning it into a valid reset.
/// </summary>
public class ConnectionSamplerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>Feeds a packet through the sampler as the receive loop would.</summary>
    private static void Observe(
        ConnectionSampler sampler,
        bool outbound,
        int payload = 100,
        uint sequence = 5000,
        double atSeconds = 0,
        ushort localPort = RawPackets.LocalPort)
    {
        using var packet = RawPackets.TcpFor(
            outbound, payload: payload, sequence: sequence, localPort: localPort, remotePort: 30000);
        using var address = RawPackets.Address(outbound);

        var info = WinDivertPacketSource.Describe(packet, address)!.Value with
        {
            Timestamp = T0.AddSeconds(atSeconds)
        };

        sampler.Observe(info, packet, address);
    }

    private static void ObserveBoth(ConnectionSampler sampler, double atSeconds = 0)
    {
        Observe(sampler, outbound: true, atSeconds: atSeconds);
        Observe(sampler, outbound: false, atSeconds: atSeconds);
    }

    [Fact]
    public void A_connection_needs_a_sample_in_both_directions()
    {
        // Half a kill leaves the connection alive at the other end, so one direction is no use.
        using var sampler = new ConnectionSampler();

        Observe(sampler, outbound: true);

        Assert.Empty(sampler.LiveConnections(T0));
    }

    [Fact]
    public void A_connection_with_both_directions_is_live()
    {
        using var sampler = new ConnectionSampler();

        ObserveBoth(sampler);

        Assert.Single(sampler.LiveConnections(T0));
    }

    [Fact]
    public void A_bare_acknowledgement_is_not_kept_as_a_template()
    {
        // It parses fine, but its sequence number sits where the stream already is rather than
        // where it is going, and resets built from one are accepted far less reliably.
        using var sampler = new ConnectionSampler();

        Observe(sampler, outbound: true, payload: 0);
        Observe(sampler, outbound: false, payload: 0);

        Assert.Empty(sampler.LiveConnections(T0));
        Assert.Equal(0, sampler.Count);
    }

    [Fact]
    public void A_silent_connection_stops_being_live()
    {
        // Resetting one that has already gone sends a stray packet to a server that moved on.
        using var sampler = new ConnectionSampler(maxIdle: TimeSpan.FromSeconds(10));

        ObserveBoth(sampler, atSeconds: 0);

        Assert.Single(sampler.LiveConnections(T0.AddSeconds(9)));
        Assert.Empty(sampler.LiveConnections(T0.AddSeconds(11)));
    }

    [Fact]
    public void Traffic_in_either_direction_keeps_a_connection_live()
    {
        using var sampler = new ConnectionSampler(maxIdle: TimeSpan.FromSeconds(10));

        ObserveBoth(sampler, atSeconds: 0);
        Observe(sampler, outbound: false, atSeconds: 8);

        Assert.Single(sampler.LiveConnections(T0.AddSeconds(15)));
    }

    [Fact]
    public void Connections_are_kept_apart()
    {
        using var sampler = new ConnectionSampler();

        ObserveBoth(sampler);
        Observe(sampler, outbound: true, localPort: 51999);
        Observe(sampler, outbound: false, localPort: 51999);

        Assert.Equal(2, sampler.Count);
        Assert.Equal(2, sampler.LiveConnections(T0).Count);
    }

    [Fact]
    public void Two_resets_are_built_per_connection_one_each_way()
    {
        // The connection is killed from both ends: outbound tells the peer this machine is
        // finished, inbound tells this machine the peer is.
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        var resets = sampler.BuildResets(T0);

        Assert.Equal(2, resets.Count);
        Assert.Contains(resets, r => r.IsOutbound);
        Assert.Contains(resets, r => !r.IsOutbound);

        foreach (var reset in resets)
        {
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_carries_the_reset_flag()
    {
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        var resets = sampler.BuildResets(T0);

        foreach (var reset in resets)
        {
            var info = WinDivertPacketSource.Describe(reset.Packet, reset.Address)!.Value;
            Assert.True(info.IsReset);
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_keeps_the_conversation_it_was_taken_from()
    {
        // The whole reason for sampling. A reset with invented ports is discarded as belonging to
        // no connection.
        using var sampler = new ConnectionSampler();
        Observe(sampler, outbound: true, sequence: 4242);
        Observe(sampler, outbound: false, sequence: 4242);

        var resets = sampler.BuildResets(T0);

        foreach (var reset in resets)
        {
            var info = WinDivertPacketSource.Describe(reset.Packet, reset.Address)!.Value;
            Assert.Equal(30000, info.RemotePort);
            Assert.Equal(RawPackets.LocalPort, info.LocalPort);
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_is_a_bare_header_with_no_payload()
    {
        // Measured on a reference limiter: exactly 40 bytes on the wire, an IPv4 header and a TCP
        // header and nothing else. A reset must not carry data, and setting the flag on a copy of
        // the sample would send that sample's payload along with it.
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        foreach (var reset in sampler.BuildResets(T0))
        {
            Assert.Equal(40, reset.Packet.Length);

            var info = WinDivertPacketSource.Describe(reset.Packet, reset.Address)!.Value;
            Assert.Equal(0, info.PayloadLength);
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_carries_no_acknowledgement()
    {
        // Matching both the reference implementation and the capture, which shows [RST] rather
        // than [RST, ACK].
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        foreach (var reset in sampler.BuildResets(T0))
        {
            var info = WinDivertPacketSource.Describe(reset.Packet, reset.Address)!.Value;
            Assert.True(info.TcpFlags!.Value.HasFlag(TcpControlFlags.Reset));
            Assert.False(info.TcpFlags.Value.HasFlag(TcpControlFlags.Ack));
            Assert.Equal(0u, info.AckNumber);
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_sequences_at_the_next_byte_that_side_would_have_sent()
    {
        // A reset is only acted on if its sequence sits inside the receiver's window. The sample's
        // own start is already behind, and an invented number is nowhere at all.
        using var sampler = new ConnectionSampler();
        Observe(sampler, outbound: true, payload: 100, sequence: 4000);
        Observe(sampler, outbound: false, payload: 250, sequence: 7000);

        var resets = sampler.BuildResets(T0);

        var outbound = resets.Single(r => r.IsOutbound);
        var inbound = resets.Single(r => !r.IsOutbound);

        Assert.Equal(
            4100u, WinDivertPacketSource.Describe(outbound.Packet, outbound.Address)!.Value.SequenceNumber);
        Assert.Equal(
            7250u, WinDivertPacketSource.Describe(inbound.Packet, inbound.Address)!.Value.SequenceNumber);

        foreach (var reset in resets)
        {
            reset.Dispose();
        }
    }

    [Fact]
    public void A_built_reset_has_a_valid_checksum()
    {
        // Setting the flag invalidates it, and a segment with a stale checksum is discarded by the
        // receiver without comment - indistinguishable from the reset silently not working.
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        var resets = sampler.BuildResets(T0);

        foreach (var reset in resets)
        {
            Assert.True(TcpChecksumIsValid(reset.Packet), "TCP checksum should verify");
            reset.Dispose();
        }
    }

    [Fact]
    public void Building_resets_leaves_the_samples_usable_again()
    {
        // Resets are clones, so firing once must not consume the templates - the module can be
        // triggered repeatedly without waiting for fresh traffic.
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        foreach (var reset in sampler.BuildResets(T0))
        {
            reset.Dispose();
        }

        var again = sampler.BuildResets(T0);

        Assert.Equal(2, again.Count);

        foreach (var reset in again)
        {
            reset.Dispose();
        }
    }

    [Fact]
    public void A_silent_connection_yields_no_resets()
    {
        using var sampler = new ConnectionSampler(maxIdle: TimeSpan.FromSeconds(10));
        ObserveBoth(sampler, atSeconds: 0);

        Assert.Empty(sampler.BuildResets(T0.AddSeconds(30)));
    }

    [Fact]
    public void The_newest_sample_in_each_direction_is_the_one_kept()
    {
        using var sampler = new ConnectionSampler();

        Observe(sampler, outbound: true, payload: 100, sequence: 100, atSeconds: 0);
        Observe(sampler, outbound: true, payload: 100, sequence: 900, atSeconds: 1);
        Observe(sampler, outbound: false, payload: 100, sequence: 500, atSeconds: 1);

        var outbound = sampler.BuildResets(T0.AddSeconds(1)).Single(r => r.IsOutbound);
        var info = WinDivertPacketSource.Describe(outbound.Packet, outbound.Address)!.Value;

        // 900 + 100 of payload: the newer sample, sequenced past its own data.
        Assert.Equal(1000u, info.SequenceNumber);
        outbound.Dispose();
    }

    [Fact]
    public void Clearing_forgets_every_connection()
    {
        using var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        sampler.Clear();

        Assert.Equal(0, sampler.Count);
        Assert.Empty(sampler.BuildResets(T0));
    }

    [Fact]
    public void Observing_after_disposal_is_ignored_rather_than_throwing()
    {
        // Teardown races the receive loop, and a packet arriving during it must not take the
        // process down.
        var sampler = new ConnectionSampler();
        sampler.Dispose();

        Observe(sampler, outbound: true);

        Assert.Equal(0, sampler.Count);
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        var sampler = new ConnectionSampler();
        ObserveBoth(sampler);

        sampler.Dispose();
        sampler.Dispose();
    }

    /// <summary>
    /// Verifies the TCP checksum the way a receiver would: the ones-complement sum over the
    /// pseudo-header and segment is 0xFFFF when the checksum in the header is correct.
    /// </summary>
    private static unsafe bool TcpChecksumIsValid(WinDivertPacket packet)
    {
        var span = packet.GetSpan(0, packet.Length);
        var ihl = (span[0] & 0x0F) * 4;
        var tcp = span[ihl..];

        uint sum = 0;

        // Pseudo-header: source and destination addresses, protocol, TCP length.
        for (var i = 12; i < 20; i += 2)
        {
            sum += (uint)((span[i] << 8) | span[i + 1]);
        }

        sum += 6;                       // protocol: TCP
        sum += (uint)tcp.Length;

        for (var i = 0; i + 1 < tcp.Length; i += 2)
        {
            sum += (uint)((tcp[i] << 8) | tcp[i + 1]);
        }

        if (tcp.Length % 2 != 0)
        {
            sum += (uint)(tcp[^1] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return sum == 0xFFFF;
    }
}
