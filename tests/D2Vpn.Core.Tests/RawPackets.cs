using System.Buffers.Binary;
using WindivertDotnet;

namespace D2Vpn.Core.Tests;

/// <summary>
/// Builds real IPv4 packets, so parsing and rewriting can be exercised on actual bytes.
/// </summary>
/// <remarks>
/// A hand-built packet is the only way to assert what the wire format actually contains. Anything
/// that constructs a <c>PacketInfo</c> directly is testing the code against its own assumptions
/// about the headers, which is exactly where a silent misreading would hide.
/// </remarks>
internal static class RawPackets
{
    public const ushort LocalPort = 51000;
    public const ushort RemotePort = 7500;

    /// <summary>PSH|ACK - a segment carrying data the peer wants delivered.</summary>
    public const byte PushAck = 0x18;

    /// <summary>Builds an IPv4 + TCP packet.</summary>
    public static WinDivertPacket Tcp(
        int payload = 100,
        byte flags = PushAck,
        uint sequence = 0x01020304,
        uint acknowledgement = 0,
        ushort localPort = LocalPort,
        ushort remotePort = RemotePort)
    {
        var total = 20 + 20 + payload;
        var packet = new WinDivertPacket(total);
        var span = packet.GetSpan(0, total);
        span.Clear();

        span[0] = 0x45;                                                   // version 4, 5 words
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)total);
        span[8] = 64;                                                     // TTL
        span[9] = 6;                                                      // protocol: TCP
        span[12] = 192; span[13] = 0; span[14] = 2; span[15] = 52;        // src 192.0.2.52
        span[16] = 10; span[17] = 0; span[18] = 0; span[19] = 1;          // dst 10.0.0.1

        var tcp = span[20..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, remotePort);           // src port
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], localPort);       // dst port
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], acknowledgement);
        tcp[12] = 0x50;                                                   // data offset: 5 words
        tcp[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 8192);           // window

        packet.Length = total;
        return packet;
    }

    /// <summary>Builds an IPv4 + UDP packet.</summary>
    public static WinDivertPacket Udp(
        int payload = 100, ushort localPort = LocalPort, ushort remotePort = 3074)
    {
        var total = 20 + 8 + payload;
        var packet = new WinDivertPacket(total);
        var span = packet.GetSpan(0, total);
        span.Clear();

        span[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)total);
        span[8] = 64;
        span[9] = 17;                                                     // protocol: UDP
        span[12] = 192; span[13] = 0; span[14] = 2; span[15] = 52;
        span[16] = 10; span[17] = 0; span[18] = 0; span[19] = 1;

        var udp = span[20..];
        BinaryPrimitives.WriteUInt16BigEndian(udp, remotePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], localPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payload));

        packet.Length = total;
        return packet;
    }

    /// <summary>An address carrying only a direction, which is all the parser reads from it.</summary>
    public static WinDivertAddress Address(bool outbound) =>
        new() { Flags = outbound ? WinDivertAddressFlag.Outbound : default };
}
