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
