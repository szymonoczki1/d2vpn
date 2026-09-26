using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using D2Vpn.Core.Network;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// An <see cref="IPacketSource"/> backed by the WinDivert kernel driver.
/// </summary>
/// <remarks>
/// <para>
/// Opens in <see cref="WinDivertFlag.Sniff"/> mode: packets are copied to us and continue up the
/// stack untouched, so nothing here can stall or break a live game. That is the correct mode for
/// calibration and diagnostics. Blocking needs divert mode, where a packet that is not reinjected
/// is dropped, and that belongs to the blocking pipeline rather than to this type.
/// </para>
/// <para>
/// Loading the driver requires elevation. Construction throws when the process is not elevated.
/// </para>
/// </remarks>
// D2Vpn.Core targets net9.0 rather than net9.0-windows so the domain types and every test that
// exercises them stay platform-neutral. Only this type actually needs Windows, so the constraint
// is declared here instead of being imposed on the whole project.
[SupportedOSPlatform("windows")]
public sealed class WinDivertPacketSource : IPacketSource
{
    private readonly WinDivert _divert;
    private bool _disposed;

    /// <inheritdoc/>
    public PortGroup Group { get; }

    /// <summary>The filter string handed to the driver. Useful in logs and diagnostics.</summary>
    public string FilterExpression { get; }

    /// <summary>Opens a sniffing handle for <paramref name="group"/>.</summary>
    /// <param name="group">Ports to observe.</param>
    /// <param name="priority">
    /// WinDivert handle priority. Only relevant when several handles match the same packet.
    /// </param>
    /// <exception cref="ArgumentException">The group's range is invalid.</exception>
    /// <exception cref="InvalidOperationException">The driver could not be opened.</exception>
    public WinDivertPacketSource(PortGroup group, short priority = 0)
    {
        Group = group.Validate();

        var filter = WinDivertFilterBuilder.Build(group);
        FilterExpression = filter.ToString() ?? string.Empty;

        try
        {
            // Sniff: copy, do not divert. RecvOnly: we never write, so the driver need not
            // reserve a send path for this handle.
            _divert = new WinDivert(filter, WinDivertLayer.Network, priority,
                WinDivertFlag.Sniff | WinDivertFlag.RecvOnly);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not open WinDivert for {group}. This needs an elevated process, and the " +
                $"WinDivert driver must be loadable. Filter was: {FilterExpression}", ex);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PacketInfo> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var packet = new WinDivertPacket();
        using var addr = new WinDivertAddress();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _divert.RecvAsync(packet, addr, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            // Parsing touches raw pointers, so it stays in a synchronous method - no pointer may
            // live across an await or a yield.
            var info = Describe(packet, addr);
            if (info is not null)
            {
                yield return info.Value;
            }
        }
    }

    /// <summary>
    /// Reduces a raw packet to a <see cref="PacketInfo"/>.
    /// </summary>
    /// <returns>
    /// The description, or <see langword="null"/> when the packet has neither a TCP nor a UDP
    /// header, or neither an IPv4 nor an IPv6 header. Such packets cannot belong to a port group
    /// and are not worth surfacing.
    /// </returns>
    private static unsafe PacketInfo? Describe(WinDivertPacket packet, WinDivertAddress addr)
    {
        var parse = packet.GetParseResult();

        ushort srcPort;
        ushort dstPort;
        if (parse.UdpHeader != null)
        {
            srcPort = parse.UdpHeader->SrcPort;
            dstPort = parse.UdpHeader->DstPort;
        }
        else if (parse.TcpHeader != null)
        {
            srcPort = parse.TcpHeader->SrcPort;
            dstPort = parse.TcpHeader->DstPort;
        }
        else
        {
            return null;
        }

        IPAddress? srcAddr;
        IPAddress? dstAddr;
        if (parse.IPV4Header != null)
        {
            srcAddr = parse.IPV4Header->SrcAddr;
            dstAddr = parse.IPV4Header->DstAddr;
        }
        else if (parse.IPV6Header != null)
        {
            srcAddr = parse.IPV6Header->SrcAddr;
            dstAddr = parse.IPV6Header->DstAddr;
        }
        else
        {
            return null;
        }

        // The driver tells us the direction; it is not inferred from ports. An inferred direction
        // would be wrong for any group whose range could contain both endpoints.
        bool outbound = addr.Flags.HasFlag(WinDivertAddressFlag.Outbound);

        return new PacketInfo
        {
            Timestamp = DateTimeOffset.UtcNow,
            Direction = outbound ? PacketDirection.Outbound : PacketDirection.Inbound,
            LocalPort = outbound ? srcPort : dstPort,
            RemotePort = outbound ? dstPort : srcPort,
            RemoteAddress = (outbound ? dstAddr : srcAddr) ?? IPAddress.None,
            PayloadLength = parse.DataLength,
            InterfaceIndex = addr.Network->IfIdx
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _divert.Dispose();
    }
}
