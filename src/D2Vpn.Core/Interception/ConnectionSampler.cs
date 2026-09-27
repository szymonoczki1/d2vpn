using System.Buffers.Binary;
using System.Net;
using System.Runtime.Versioning;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Keeps one recent packet per direction per connection, to build new packets from.
/// </summary>
/// <remarks>
/// <para>
/// A reset cannot be conjured from nothing. To be accepted it has to carry the addresses, ports and
/// sequence numbers of a real conversation, so the only practical way to build one is to take a
/// packet that connection recently sent and change it. That means keeping a sample.
/// </para>
/// <para>
/// One per direction, because a connection is killed from both ends: an outbound reset tells the
/// peer this machine is finished, and an inbound one tells this machine the peer is. They are built
/// from different samples because each has to look like it came from the side it claims.
/// </para>
/// <para>
/// Only packets carrying payload are kept. A bare acknowledgement is a valid TCP segment but a poor
/// template - its sequence number is not advancing, and the reference implementation skips them for
/// the same reason.
/// </para>
/// <para>
/// Samples are clones. The receive loop reuses one packet and one address for every read, so
/// keeping the originals would mean every sample pointing at whatever arrived most recently.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConnectionSampler : IDisposable
{
    private readonly Dictionary<Connection, Sample> _samples = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>How long a connection may be silent and still count as live.</summary>
    /// <remarks>
    /// A connection that has said nothing for this long is probably already gone, and resetting it
    /// achieves nothing except sending a stray packet to a server that has moved on. Ten seconds is
    /// the reference implementation's threshold.
    /// </remarks>
    public TimeSpan MaxIdle { get; }

    /// <summary>Connections currently sampled.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>Creates a sampler.</summary>
    public ConnectionSampler(TimeSpan? maxIdle = null) =>
        MaxIdle = maxIdle ?? TimeSpan.FromSeconds(10);

    /// <summary>Notes a packet, keeping it if it is a better template than the last.</summary>
    public void Observe(PacketInfo info, WinDivertPacket packet, WinDivertAddress address)
    {
        // Payload only. A bare ACK parses fine and resets built from one are accepted far less
        // reliably, because its sequence number sits where the stream already is rather than
        // where it is going.
        if (info.PayloadLength == 0 || info.SequenceNumber is null)
        {
            return;
        }

        var key = new Connection(info.RemoteAddress, info.LocalPort, info.RemotePort);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_samples.TryGetValue(key, out var sample))
            {
                sample = new Sample();
                _samples[key] = sample;
            }

            sample.Replace(info.IsOutbound, packet, address, info.Timestamp);
        }
    }

    /// <summary>
    /// The connections that have both samples and have spoken recently enough to be worth resetting.
    /// </summary>
    public IReadOnlyList<Connection> LiveConnections(DateTimeOffset now)
    {
        lock (_gate)
        {
            return LiveConnectionsCore(now);
        }
    }

    /// <summary>
    /// Builds resets for every live connection, which the caller owns and must dispose.
    /// </summary>
    /// <remarks>
    /// Two per connection, outbound first. Returned rather than sent so that every write to the
    /// handle stays on the receive loop's thread - this is triggered from a hotkey.
    /// </remarks>
    public IReadOnlyList<Reset> BuildResets(DateTimeOffset now)
    {
        lock (_gate)
        {
            var resets = new List<Reset>();

            foreach (var key in LiveConnectionsCore(now))
            {
                var sample = _samples[key];

                if (TryBuild(sample.Outbound!, sample.OutboundAddress!, out var outbound))
                {
                    resets.Add(new Reset(key, outbound!, sample.OutboundAddress!.Clone(), true));
                }

                if (TryBuild(sample.Inbound!, sample.InboundAddress!, out var inbound))
                {
                    resets.Add(new Reset(key, inbound!, sample.InboundAddress!.Clone(), false));
                }
            }

            return resets;
        }
    }

    /// <summary>
    /// Builds a bare reset from a sample's addressing. Callers hold the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed rather than cloned. Setting the flag on a copy of the sample would send a reset
    /// carrying that sample's payload, and a reset must not carry data - receivers discard one that
    /// does. A capture of a reference limiter doing this shows exactly 40 bytes on the wire, which
    /// is an IPv4 header and a TCP header and nothing else.
    /// </para>
    /// <para>
    /// The sequence number is the sample's, plus its payload - the next byte that side would have
    /// sent. A reset is only acted on if its sequence sits inside the receiver's window, so the
    /// sample's own start is already behind and an invented number is nowhere at all.
    /// </para>
    /// <para>
    /// No acknowledgement, matching both the reference implementation and the capture, which shows
    /// <c>[RST]</c> rather than <c>[RST, ACK]</c>.
    /// </para>
    /// </remarks>
    private static unsafe bool TryBuild(
        WinDivertPacket sample, WinDivertAddress address, out WinDivertPacket? reset)
    {
        const int HeaderBytes = 40;
        reset = null;

        try
        {
            var parse = sample.GetParseResult();

            // IPv4 only. The game's TCP traffic is IPv4 here, and a v6 reset needs a different
            // header this has never had a sample of to check against.
            if (parse.TcpHeader == null || parse.IPV4Header == null)
            {
                return false;
            }

            var source = parse.IPV4Header->SrcAddr;
            var destination = parse.IPV4Header->DstAddr;
            var sourcePort = parse.TcpHeader->SrcPort;
            var destinationPort = parse.TcpHeader->DstPort;
            var window = parse.TcpHeader->Window;
            var sequence = parse.TcpHeader->SeqNum + (uint)parse.DataLength;

            var packet = new WinDivertPacket(HeaderBytes);
            var span = packet.GetSpan(0, HeaderBytes);
            span.Clear();

            span[0] = 0x45;                                     // IPv4, five 32-bit words
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], HeaderBytes);
            span[8] = 128;                                      // TTL
            span[9] = 6;                                        // protocol: TCP
            source.TryWriteBytes(span[12..16], out _);
            destination.TryWriteBytes(span[16..20], out _);

            var tcp = span[20..];
            BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], destinationPort);
            BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
            BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 0);  // no acknowledgement
            tcp[12] = 0x50;                                      // five 32-bit words
            tcp[13] = 0x04;                                      // RST alone
            BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], window);

            packet.Length = HeaderBytes;

            // Without this the receiver discards it without comment, which looks exactly like the
            // reset never having been sent.
            packet.CalcChecksums(address);

            reset = packet;
            return true;
        }
        catch
        {
            // A sample that will not parse is one connection not killed, not a reason to abandon
            // the others.
            return false;
        }
    }

    /// <summary>
    /// Connections worth resetting. Callers hold the lock.
    /// </summary>
    /// <remarks>
    /// Both directions or neither: a reset needs a template from the side it claims to come from,
    /// and half a kill leaves the connection alive at the other end.
    /// </remarks>
    private List<Connection> LiveConnectionsCore(DateTimeOffset now)
    {
        var live = new List<Connection>();

        foreach (var (key, sample) in _samples)
        {
            if (sample.Outbound is not null
                && sample.Inbound is not null
                && now - sample.LastSeen <= MaxIdle)
            {
                live.Add(key);
            }
        }

        return live;
    }

    /// <summary>Forgets every connection.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var sample in _samples.Values)
            {
                sample.Dispose();
            }

            _samples.Clear();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var sample in _samples.Values)
            {
                sample.Dispose();
            }

            _samples.Clear();
        }
    }

    /// <summary>One TCP connection: a peer, and the two ports talking.</summary>
    public readonly record struct Connection(IPAddress Address, int LocalPort, int RemotePort)
    {
        /// <inheritdoc/>
        public override string ToString() => $"{Address}:{RemotePort} (local {LocalPort})";
    }

    /// <summary>A reset ready to inject, which the caller owns.</summary>
    public sealed class Reset(
        Connection connection, WinDivertPacket packet, WinDivertAddress address, bool outbound)
        : IDisposable
    {
        /// <summary>The connection it will kill.</summary>
        public Connection Connection { get; } = connection;

        /// <summary>The packet to send.</summary>
        public WinDivertPacket Packet { get; } = packet;

        /// <summary>The address to send it with, which carries the direction.</summary>
        public WinDivertAddress Address { get; } = address;

        /// <summary>
        /// True for the reset sent to the peer, false for the one delivered to this machine.
        /// </summary>
        public bool IsOutbound { get; } = outbound;

        /// <inheritdoc/>
        public void Dispose()
        {
            Packet.Dispose();
            Address.Dispose();
        }
    }

    /// <summary>The most recent usable packet in each direction on one connection.</summary>
    private sealed class Sample : IDisposable
    {
        public WinDivertPacket? Outbound { get; private set; }

        public WinDivertAddress? OutboundAddress { get; private set; }

        public WinDivertPacket? Inbound { get; private set; }

        public WinDivertAddress? InboundAddress { get; private set; }

        /// <summary>When this connection last carried anything, either way.</summary>
        public DateTimeOffset LastSeen { get; private set; }

        public void Replace(
            bool outbound, WinDivertPacket packet, WinDivertAddress address, DateTimeOffset at)
        {
            LastSeen = at;

            if (outbound)
            {
                Outbound?.Dispose();
                OutboundAddress?.Dispose();
                Outbound = packet.Clone();
                OutboundAddress = address.Clone();
            }
            else
            {
                Inbound?.Dispose();
                InboundAddress?.Dispose();
                Inbound = packet.Clone();
                InboundAddress = address.Clone();
            }
        }

        public void Dispose()
        {
            Outbound?.Dispose();
            OutboundAddress?.Dispose();
            Inbound?.Dispose();
            InboundAddress?.Dispose();
            Outbound = null;
            Inbound = null;
            OutboundAddress = null;
            InboundAddress = null;
        }
    }
}
