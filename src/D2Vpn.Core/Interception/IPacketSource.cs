using D2Vpn.Core.Network;

namespace D2Vpn.Core.Interception;

/// <summary>
/// A stream of packets for one <see cref="PortGroup"/>.
/// </summary>
/// <remarks>
/// Deliberately observation-only. Dropping, delaying and injecting arrive with the blocking
/// pipeline and will extend this, not replace it - calibration and diagnostics never need to
/// modify traffic, and keeping them on a read-only path means they cannot break a live game.
/// </remarks>
public interface IPacketSource : IDisposable
{
    /// <summary>The group this source is filtering on.</summary>
    PortGroup Group { get; }

    /// <summary>
    /// Yields packets as they arrive, until <paramref name="cancellationToken"/> is signalled.
    /// </summary>
    IAsyncEnumerable<PacketInfo> ReadAllAsync(CancellationToken cancellationToken = default);
}
