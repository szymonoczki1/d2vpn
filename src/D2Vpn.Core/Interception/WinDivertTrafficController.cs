using System.Runtime.Versioning;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Runs a module against live traffic, in divert mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first thing in the project that can break a connection.</b> In divert mode a
/// received packet has already been taken out of the network stack: it reaches its destination
/// only if it is reinjected. So the failure mode is not a wrong number on a report, it is a dead
/// connection - and every design decision here is about that.
/// </para>
/// <list type="bullet">
/// <item>
/// A packet the module does not rule on is passed, never dropped. An exception mid-decision,
/// an unparseable packet, a module that throws - all of them let traffic through.
/// </item>
/// <item>
/// Closing the handle restores normal traffic, so disposal is the kill switch. It is called on
/// cancellation, on unhandled errors, and on any exit path.
/// </item>
/// <item>
/// Nothing is buffered. Every packet is decided and either reinjected or dropped immediately,
/// so there is no queue to lose if the process dies.
/// </item>
/// </list>
/// <para>
/// Only one process should divert a given port range at a time. Two handles with overlapping
/// filters both receive every matching packet and can each drop or reinject it independently, so
/// any other limiter must be fully exited - not merely idle - before this runs.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertTrafficController : IDisposable
{
    private readonly WinDivert _divert;
    private readonly ITrafficModule _module;
    private bool _disposed;

    /// <summary>The module deciding each packet.</summary>
    public ITrafficModule Module => _module;

    /// <summary>The filter string handed to the driver.</summary>
    public string FilterExpression { get; }

    /// <summary>Packets seen since the controller started.</summary>
    public long PacketsSeen { get; private set; }

    /// <summary>Packets dropped since the controller started.</summary>
    public long PacketsDropped { get; private set; }

    /// <summary>Outbound packets seen.</summary>
    public long OutboundSeen { get; private set; }

    /// <summary>Outbound packets dropped.</summary>
    public long OutboundDropped { get; private set; }

    /// <summary>Inbound packets seen.</summary>
    public long InboundSeen { get; private set; }

    /// <summary>
    /// Inbound packets dropped.
    /// </summary>
    /// <remarks>
    /// Totals alone hide which direction a module actually acted on. A run that dropped 62% of
    /// everything could be blocking all of one direction or most of both, and those are very
    /// different bugs.
    /// </remarks>
    public long InboundDropped { get; private set; }

    /// <summary>
    /// Packets passed because something went wrong rather than because the module said so.
    /// </summary>
    /// <remarks>
    /// A non-zero value here is not fatal - traffic kept flowing, which is the point - but it
    /// means the module is not seeing everything it should, so it is worth surfacing.
    /// </remarks>
    public long PacketsPassedOnError { get; private set; }

    /// <summary>
    /// Raised for each packet that crosses while the module is holding.
    /// </summary>
    /// <remarks>
    /// The diagnostic for "it usually works but sometimes does not". A hold that is 95% effective
    /// fails 5% of the time, and which 5% is invisible from the totals - but a timestamped list of
    /// exactly what got through can be lined up against the moment the trick failed.
    ///
    /// Only fires while holding, so it stays quiet during a warmup or after a release.
    /// </remarks>
    public event Action<PacketInfo>? PacketPassedWhileHolding;

    /// <summary>Opens a diverting handle for the module's port group.</summary>
    /// <param name="module">Decides each packet.</param>
    /// <param name="priority">
    /// Handle priority, relevant only when several handles match the same packet.
    /// </param>
    /// <exception cref="InvalidOperationException">The driver could not be opened.</exception>
    public WinDivertTrafficController(ITrafficModule module, short priority = 0)
    {
        _module = module;

        var filter = WinDivertFilterBuilder.Build(module.Group);
        FilterExpression = filter.ToString() ?? string.Empty;

        try
        {
            // No Sniff flag: this genuinely diverts. A packet not reinjected does not arrive.
            _divert = new WinDivert(filter, WinDivertLayer.Network, priority, WinDivertFlag.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not open WinDivert for {module.Group} in divert mode. This needs an "
                + "elevated process, and no other limiter may be holding an overlapping filter. "
                + $"Filter was: {FilterExpression}", ex);
        }
    }

    /// <summary>
    /// Receives, decides and reinjects until cancelled.
    /// </summary>
    /// <remarks>
    /// Returns only when <paramref name="cancellationToken"/> is signalled or the handle fails.
    /// Either way the handle is disposed on the way out, which restores normal traffic.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var packet = new WinDivertPacket();
        using var addr = new WinDivertAddress();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _divert.RecvAsync(packet, addr, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                PacketsSeen++;

                var outbound = addr.Flags.HasFlag(WinDivertAddressFlag.Outbound);
                if (outbound)
                {
                    OutboundSeen++;
                }
                else
                {
                    InboundSeen++;
                }

                if (ShouldPass(packet, addr))
                {
                    await _divert.SendAsync(packet, addr).ConfigureAwait(false);
                }
                else
                {
                    PacketsDropped++;
                    if (outbound)
                    {
                        OutboundDropped++;
                    }
                    else
                    {
                        InboundDropped++;
                    }
                }
            }
        }
        finally
        {
            // The handle must close on every exit path. While it is open the driver keeps handing
            // packets to a loop that is no longer reading them, and the connection stalls.
            Dispose();
        }
    }

    /// <summary>
    /// Asks the module about a packet, defaulting to passing it whenever anything goes wrong.
    /// </summary>
    private bool ShouldPass(WinDivertPacket packet, WinDivertAddress addr)
    {
        try
        {
            var info = WinDivertPacketSource.Describe(packet, addr);
            if (info is null)
            {
                // Neither TCP nor UDP, or no IP header. It cannot belong to a port group, so it is
                // none of our business and must not be swallowed.
                PacketsPassedOnError++;
                return true;
            }

            var verdict = _module.Decide(info.Value);

            if (verdict == PacketVerdict.Pass && _module.IsActive)
            {
                PacketPassedWhileHolding?.Invoke(info.Value);
            }

            return verdict == PacketVerdict.Pass;
        }
        catch
        {
            // A module bug must not cost the user their connection.
            PacketsPassedOnError++;
            return true;
        }
    }

    /// <summary>
    /// Closes the handle, restoring normal traffic. This is the kill switch.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _module.Reset();
        _divert.Dispose();
    }
}
