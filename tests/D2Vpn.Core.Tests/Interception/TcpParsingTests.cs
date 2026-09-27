using System.Buffers.Binary;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;
using WindivertDotnet;

namespace D2Vpn.Core.Tests.Interception;

/// <summary>
/// Reads real IPv4/TCP bytes through the same path a diverted packet takes.
/// </summary>
/// <remarks>
/// <para>
/// Written for one question that could not be answered from the library's documentation: whether
/// the sequence number comes back in host order. Matching a retransmission works either way, since
/// that only compares one sequence number against another - but the capacity check does arithmetic
/// on it, and a byte-swapped value would make that silently meaningless rather than obviously
/// broken. So it is asserted against a known constant rather than assumed.
/// </para>
/// <para>
/// Everything here is hand-built bytes, so it also covers the parsing itself: a header misread
/// here would surface as a module that quietly never matches anything.
/// </para>
/// </remarks>
public class TcpParsingTests
{
    private const uint KnownSequence = 0x01020304;
    private const ushort LocalPort = 51000;
    private const ushort RemotePort = 7500;

    /// <summary>Builds an outbound IPv4 + TCP packet with a payload of <paramref name="payload"/> bytes.</summary>
    private static WinDivertPacket Build(
        int payload, byte flags, uint sequence = KnownSequence)
    {
        var total = 20 + 20 + payload;
        var packet = new WinDivertPacket(total);
        var span = packet.GetSpan(0, total);
        span.Clear();

        // IPv4
        span[0] = 0x45;                                                   // version 4, 5 words
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)total);  // total length
        span[8] = 64;                                                     // TTL
        span[9] = 6;                                                      // protocol: TCP
        span[12] = 10; span[13] = 0; span[14] = 0; span[15] = 1;          // src 10.0.0.1
        span[16] = 192; span[17] = 0; span[18] = 2; span[19] = 1;         // dst 192.0.2.1

        // TCP
        var tcp = span[20..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], RemotePort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);        // seq
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 0);               // ack
        tcp[12] = 0x50;                                                   // data offset: 5 words
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 8192);           // window

        packet.Length = total;
        return packet;
    }

    private static PacketInfo? Describe(WinDivertPacket packet, bool outbound = true)
    {
        using var addr = new WinDivertAddress();
        addr.Flags = outbound ? WinDivertAddressFlag.Outbound : default;
        return WinDivertPacketSource.Describe(packet, addr);
    }

    [Fact]
    public void The_sequence_number_comes_back_in_host_order()
    {
        // The assertion this file exists for. If the library handed back network order this would
        // read 0x04030201, and every piece of sequence arithmetic would be quietly wrong.
        using var packet = Build(payload: 100, flags: 0x18);   // PSH|ACK

        var info = Describe(packet);

        Assert.NotNull(info);
        Assert.Equal(KnownSequence, info!.Value.SequenceNumber);
    }

    [Fact]
    public void The_end_of_a_segment_is_its_sequence_plus_its_payload()
    {
        using var packet = Build(payload: 100, flags: 0x18);

        var info = Describe(packet);

        Assert.Equal(100, info!.Value.PayloadLength);
        Assert.Equal(KnownSequence + 100u, info.Value.SequenceEnd);
    }

    [Theory]
    [InlineData(0x02, TcpControlFlags.Syn)]
    [InlineData(0x04, TcpControlFlags.Reset)]
    [InlineData(0x08, TcpControlFlags.Push)]
    [InlineData(0x10, TcpControlFlags.Ack)]
    [InlineData(0x01, TcpControlFlags.Fin)]
    public void Each_control_bit_is_read_from_its_own_position(byte wire, TcpControlFlags expected)
    {
        using var packet = Build(payload: 0, flags: wire);

        var info = Describe(packet);

        Assert.Equal(expected, info!.Value.TcpFlags);
    }

    [Fact]
    public void Combined_flags_are_all_reported()
    {
        using var packet = Build(payload: 10, flags: 0x18);   // PSH|ACK

        var flags = Describe(packet)!.Value.TcpFlags;

        Assert.True(flags!.Value.HasFlag(TcpControlFlags.Push));
        Assert.True(flags.Value.HasFlag(TcpControlFlags.Ack));
        Assert.False(flags.Value.HasFlag(TcpControlFlags.Reset));
    }

    [Fact]
    public void A_pushed_segment_with_payload_counts_as_data()
    {
        using var packet = Build(payload: 100, flags: 0x18);

        Assert.True(Describe(packet)!.Value.IsTcpData);
    }

    [Fact]
    public void A_bare_ack_is_not_data()
    {
        // Blocking these stalls the conversation without withholding anything worth withholding.
        using var packet = Build(payload: 0, flags: 0x10);

        Assert.False(Describe(packet)!.Value.IsTcpData);
    }

    [Fact]
    public void A_pushed_segment_with_no_payload_is_not_data()
    {
        using var packet = Build(payload: 0, flags: 0x18);

        Assert.False(Describe(packet)!.Value.IsTcpData);
    }

    [Fact]
    public void A_reset_is_recognised()
    {
        // The one flag that has to force a module off: on this game a reset surfaces as an error
        // code, so treating it as ordinary traffic means holding through a dead connection.
        using var packet = Build(payload: 0, flags: 0x14);   // RST|ACK

        Assert.True(Describe(packet)!.Value.IsReset);
    }

    [Fact]
    public void The_remote_port_is_taken_from_the_direction_rather_than_inferred()
    {
        using var packet = Build(payload: 10, flags: 0x18);

        var outbound = Describe(packet, outbound: true)!.Value;
        var inbound = Describe(packet, outbound: false)!.Value;

        Assert.Equal(RemotePort, outbound.RemotePort);
        Assert.Equal(LocalPort, outbound.LocalPort);

        // The same bytes read the other way round: what was the destination is now the source.
        Assert.Equal(LocalPort, inbound.RemotePort);
        Assert.Equal(RemotePort, inbound.LocalPort);
    }

    [Fact]
    public void Udp_carries_no_sequence_or_flags()
    {
        // The fields are nullable so that a UDP module cannot accidentally read a zero as a real
        // sequence number.
        var total = 20 + 8;
        using var packet = new WinDivertPacket(total);
        var span = packet.GetSpan(0, total);
        span.Clear();
        span[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)total);
        span[8] = 64;
        span[9] = 17;                                          // protocol: UDP
        span[12] = 10; span[15] = 1;
        span[16] = 192; span[17] = 0; span[18] = 2; span[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(span[20..], LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(span[22..], 3074);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 8);   // UDP length
        packet.Length = total;

        var info = Describe(packet);

        Assert.NotNull(info);
        Assert.Null(info!.Value.SequenceNumber);
        Assert.Null(info.Value.TcpFlags);
        Assert.False(info.Value.IsTcpData);
        Assert.False(info.Value.IsReset);
    }
}
