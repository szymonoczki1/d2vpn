using System.Net;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests;

/// <summary>Builds <see cref="PacketInfo"/> values for tests without needing a driver.</summary>
internal static class TestPackets
{
    private static readonly IPAddress DefaultPeer = IPAddress.Parse("192.0.2.52");

    /// <summary>A second peer, for exercising per-peer analysis.</summary>
    public static readonly IPAddress OtherPeer = IPAddress.Parse("192.0.2.33");

    /// <summary>Fixed origin so a test can express times as seconds from the start of a run.</summary>
    public static readonly DateTimeOffset Origin = DateTimeOffset.UnixEpoch;

    public static PacketInfo Outbound(
        int remotePort, int payloadLength, double atSeconds = 0, IPAddress? peer = null) =>
        Make(PacketDirection.Outbound, remotePort, payloadLength, atSeconds, peer);

    public static PacketInfo Inbound(
        int remotePort, int payloadLength, double atSeconds = 0, IPAddress? peer = null) =>
        Make(PacketDirection.Inbound, remotePort, payloadLength, atSeconds, peer);

    /// <summary>An inbound TCP segment carrying data, as a 7500 hold would see it.</summary>
    public static PacketInfo TcpData(
        int remotePort, int payloadLength, uint sequence, double atSeconds = 0,
        IPAddress? peer = null, int localPort = 51234) =>
        Tcp(PacketDirection.Inbound, remotePort, payloadLength, sequence,
            TcpControlFlags.Push | TcpControlFlags.Ack, atSeconds, peer, localPort);

    /// <summary>An inbound bare acknowledgement - a TCP segment carrying nothing.</summary>
    public static PacketInfo TcpAck(
        int remotePort, uint sequence, double atSeconds = 0, IPAddress? peer = null) =>
        Tcp(PacketDirection.Inbound, remotePort, 0, sequence, TcpControlFlags.Ack, atSeconds, peer);

    /// <summary>An inbound reset, which is a peer abandoning the connection.</summary>
    public static PacketInfo TcpReset(
        int remotePort, double atSeconds = 0, IPAddress? peer = null) =>
        Tcp(PacketDirection.Inbound, remotePort, 0, 0,
            TcpControlFlags.Reset | TcpControlFlags.Ack, atSeconds, peer);

    /// <summary>An outbound TCP segment.</summary>
    public static PacketInfo TcpOutbound(
        int remotePort, int payloadLength, uint sequence = 1, double atSeconds = 0,
        IPAddress? peer = null) =>
        Tcp(PacketDirection.Outbound, remotePort, payloadLength, sequence,
            TcpControlFlags.Push | TcpControlFlags.Ack, atSeconds, peer);

    private static PacketInfo Tcp(
        PacketDirection direction, int remotePort, int payloadLength, uint sequence,
        TcpControlFlags flags, double atSeconds, IPAddress? peer, int localPort = 51234) =>
        new()
        {
            Timestamp = Origin.AddSeconds(atSeconds),
            Direction = direction,
            LocalPort = localPort,
            RemotePort = remotePort,
            RemoteAddress = peer ?? DefaultPeer,
            PayloadLength = payloadLength,
            InterfaceIndex = 0,
            SequenceNumber = sequence,
            TcpFlags = flags
        };

    private static PacketInfo Make(
        PacketDirection direction, int remotePort, int payloadLength, double atSeconds,
        IPAddress? peer = null) =>
        new()
        {
            Timestamp = Origin.AddSeconds(atSeconds),
            Direction = direction,
            LocalPort = 51234,
            RemotePort = remotePort,
            RemoteAddress = peer ?? DefaultPeer,
            PayloadLength = payloadLength,
            InterfaceIndex = 0
        };
}
