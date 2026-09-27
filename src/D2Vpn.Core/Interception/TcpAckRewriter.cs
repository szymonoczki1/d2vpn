using System.Net;
using System.Runtime.Versioning;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Brings a withheld TCP segment's acknowledgement number up to date before it is replayed.
/// </summary>
/// <remarks>
/// <para>
/// A UDP packet can be replayed exactly as it was captured; a TCP segment cannot. Every segment
/// carries an acknowledgement of what its sender had received at the moment it was built, and a
/// segment held for several seconds is acknowledging a position this connection has long since
/// passed. Replaying it verbatim delivers a duplicate acknowledgement - and three of those are the
/// signal for fast retransmit, so this machine would start re-sending data the peer already has.
/// </para>
/// <para>
/// So the field is rewritten to the highest acknowledgement seen on that connection, which is the
/// reference implementation's behaviour (<c>p.AckNum = 0; // let storepacket assign highest</c>).
/// The sequence number is deliberately left alone: that identifies the data being delivered, which
/// is the entire point of replaying it, and the receiver is still waiting for exactly that.
/// </para>
/// <para>
/// Rewriting the header invalidates the checksum, so it is recalculated. A segment with a stale
/// checksum is discarded by the receiver without comment, which would look exactly like the replay
/// silently not working.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TcpAckRewriter
{
    private readonly Dictionary<Connection, uint> _highest = [];
    private readonly Lock _gate = new();

    /// <summary>Segments whose acknowledgement number was brought forward.</summary>
    public long Rewritten { get; private set; }

    /// <summary>Segments replayed unchanged because nothing newer had been seen.</summary>
    public long LeftAlone { get; private set; }

    /// <summary>Notes the acknowledgement number of a packet passing through.</summary>
    /// <remarks>
    /// Called for every packet, held or not, so that the value is current when a replay happens.
    /// Only the sender's own direction is tracked: an inbound segment's acknowledgement field is
    /// written by the peer, so the newest inbound one is the best guess at what the peer would put
    /// there now.
    /// </remarks>
    public void Observe(PacketInfo packet)
    {
        if (packet.AckNumber is not { } ack || packet.IsOutbound)
        {
            return;
        }

        var key = new Connection(packet.RemoteAddress, packet.LocalPort);

        lock (_gate)
        {
            // Sequence space wraps, so "newer" is a signed comparison rather than a larger number.
            if (!_highest.TryGetValue(key, out var current) || IsAfter(ack, current))
            {
                _highest[key] = ack;
            }
        }
    }

    /// <summary>Rewrites a held segment's acknowledgement number, if a newer one is known.</summary>
    public unsafe void Rewrite(WinDivertPacket packet, WinDivertAddress address)
    {
        var info = WinDivertPacketSource.Describe(packet, address);
        if (info is null || info.Value.AckNumber is null)
        {
            return;
        }

        var key = new Connection(info.Value.RemoteAddress, info.Value.LocalPort);
        uint highest;

        lock (_gate)
        {
            if (!_highest.TryGetValue(key, out highest))
            {
                LeftAlone++;
                return;
            }
        }

        if (!IsAfter(highest, info.Value.AckNumber.Value))
        {
            LeftAlone++;
            return;
        }

        var parse = packet.GetParseResult();
        if (parse.TcpHeader == null)
        {
            return;
        }

        parse.TcpHeader->AckNum = highest;

        // The header changed, so the checksum is now wrong and the receiver would drop the segment
        // without saying anything - indistinguishable from the replay not happening.
        packet.CalcChecksums(address);

        lock (_gate)
        {
            Rewritten++;
        }
    }

    /// <summary>Forgets every connection.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _highest.Clear();
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is later than <paramref name="current"/> in TCP
    /// sequence space.
    /// </summary>
    /// <remarks>
    /// Sequence numbers are 32 bits and wrap, so a plain greater-than is wrong near the wrap point:
    /// a number just past it is numerically tiny and genuinely newer. The signed difference is the
    /// standard comparison and handles the wrap by construction.
    /// </remarks>
    private static bool IsAfter(uint candidate, uint current) => (int)(candidate - current) > 0;

    /// <summary>One TCP connection, which is a peer and the local port talking to it.</summary>
    private readonly record struct Connection(IPAddress Address, int LocalPort);
}
