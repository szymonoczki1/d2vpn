using System.Net;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Interception;

/// <summary>
/// One observed packet, reduced to the facts a module or the calibrator needs.
/// </summary>
/// <remarks>
/// This type carries no WinDivert handles and no unmanaged memory, so modules can be exercised in
/// tests against synthetic packets with no driver present. Anything needing the raw bytes works
/// against the source directly.
/// </remarks>
public readonly record struct PacketInfo
{
    /// <summary>When the packet was observed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Travel direction relative to this machine.</summary>
    public required PacketDirection Direction { get; init; }

    /// <summary>Port on this machine. Ephemeral and of little interest beyond diagnostics.</summary>
    public required int LocalPort { get; init; }

    /// <summary>Port on the peer. This is what a <see cref="PortGroup"/> matches on.</summary>
    public required int RemotePort { get; init; }

    /// <summary>Address of the peer. Used to keep per-peer state.</summary>
    public required IPAddress RemoteAddress { get; init; }

    /// <summary>
    /// Transport payload size in bytes, excluding IP and TCP/UDP headers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Payload, never frame.</b> Frame length includes a link-layer header whose size depends on
    /// the adapter - a Raw IP VPN tunnel has none, Ethernet adds 14 bytes. A signature expressed in
    /// frame bytes would therefore stop matching the moment the VPN is toggled. Payload length is
    /// invariant across both paths, which is why every signature in d2vpn is expressed in it.
    /// </para>
    /// </remarks>
    public required int PayloadLength { get; init; }

    /// <summary>
    /// Index of the network interface the driver saw this packet on.
    /// </summary>
    /// <remarks>
    /// Kept for diagnostics, never for filtering. A packet leaving through a VPN tunnel can cross
    /// the stack more than once, and the interface index is what distinguishes those sightings -
    /// but a filter that keys on it would break the moment the tunnel goes away.
    /// </remarks>
    public required int InterfaceIndex { get; init; }

    /// <summary>
    /// The TCP sequence number, or <see langword="null"/> for anything that is not TCP.
    /// </summary>
    /// <remarks>
    /// Identity for a TCP segment. Blocking one does not make it go away - the sender re-sends it,
    /// and the retransmission is a different packet carrying the same sequence number. A module
    /// that blocks TCP without recognising that leaks the moment the first retransmission arrives,
    /// roughly 200 ms later. UDP has no equivalent and needs none: nothing re-sends it.
    /// </remarks>
    public uint? SequenceNumber { get; init; }

    /// <summary>The TCP flags, or <see langword="null"/> for anything that is not TCP.</summary>
    public TcpControlFlags? TcpFlags { get; init; }

    /// <summary>The sequence number just past this segment's payload.</summary>
    /// <remarks>
    /// What the peer will acknowledge next if it receives this segment, so the natural way to ask
    /// how much contiguous data a set of blocked segments covers.
    /// </remarks>
    public uint? SequenceEnd =>
        SequenceNumber is { } seq ? seq + (uint)PayloadLength : null;

    /// <summary>True for a TCP segment carrying data the peer wants delivered.</summary>
    /// <remarks>
    /// The thing worth blocking on a TCP port. A bare ACK carries nothing, and blocking those
    /// stalls the conversation without withholding anything.
    /// </remarks>
    public bool IsTcpData =>
        PayloadLength > 0 && TcpFlags?.HasFlag(TcpControlFlags.Push) == true;

    /// <summary>True for an inbound reset, which is the peer giving up on the connection.</summary>
    public bool IsReset => TcpFlags?.HasFlag(TcpControlFlags.Reset) == true;

    /// <summary>True when this packet travelled from this machine to the peer ("UL").</summary>
    public bool IsOutbound => Direction == PacketDirection.Outbound;

    /// <summary>True when this packet arrived from the peer ("DL").</summary>
    public bool IsInbound => Direction == PacketDirection.Inbound;
}
