using System.Net;
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
/// Buffering is opt-in. Without a buffer every packet is decided and either reinjected or dropped
/// immediately, so there is no queue to lose if the process dies. With one, blocked packets are
/// held - and replayed either on release, or, with a slow delay set, once they have been held long
/// enough. Either way they are discarded, not flushed, if the handle closes instead.
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
    private readonly PacketBuffer? _buffer;
    private bool _wasHolding;
    private bool _disposed;

    /// <summary>The module deciding each packet.</summary>
    public ITrafficModule Module => _module;

    /// <summary>The filter string handed to the driver.</summary>
    public string FilterExpression { get; }

    /// <summary>Packets seen since the controller started.</summary>
    public long PacketsSeen { get; private set; }

    /// <summary>Packets dropped since the controller started.</summary>
    public long PacketsDropped { get; private set; }

    /// <summary>Packets held for replay since the controller started.</summary>
    public long PacketsBuffered { get; private set; }

    /// <summary>Packets replayed on release since the controller started.</summary>
    public long PacketsReplayed { get; private set; }

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

    /// <summary>
    /// Raised after a drain, with how many packets went out and how long the drain took.
    /// </summary>
    /// <remarks>
    /// The duration is the diagnostic, not the count. Every replayed packet is an await on the
    /// same thread that receives, so a drain is a window in which nothing is being read - and the
    /// driver queues what arrives meanwhile, then discards it if the queue fills. A long drain can
    /// therefore cost live traffic on a connection that was otherwise healthy, which would make a
    /// large backlog harmful for reasons that have nothing to do with the staleness of what is in
    /// it. Surfacing both numbers is what separates those two explanations.
    /// </remarks>
    public event Action<int, TimeSpan>? BufferDrained;

    /// <summary>Packets sent by the longest single drain so far.</summary>
    public int LargestDrain { get; private set; }

    /// <summary>How long the slowest single drain took.</summary>
    public TimeSpan SlowestDrain { get; private set; }

    /// <summary>Opens a diverting handle for the module's port group.</summary>
    /// <param name="module">Decides each packet.</param>
    /// <param name="priority">
    /// Handle priority, relevant only when several handles match the same packet.
    /// </param>
    /// <exception cref="InvalidOperationException">The driver could not be opened.</exception>
    /// <param name="buffer">
    /// Where blocked packets are held for replay, or <see langword="null"/> to drop them.
    /// </param>
    public WinDivertTrafficController(
        ITrafficModule module, short priority = 0, PacketBuffer? buffer = null)
    {
        _module = module;
        _buffer = buffer;

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

                // Draining happens here rather than wherever the release came from, so that every
                // write to the handle stays on this thread: a hold is released from a hotkey. It
                // also runs before this packet is sent, so the held actions arrive ahead of the
                // live one that follows them rather than behind it.
                if (_buffer is not null)
                {
                    if (_buffer.Schedule is not null)
                    {
                        await DrainDueAsync().ConfigureAwait(false);
                    }

                    var holding = _module.IsActive;
                    if (_wasHolding && !holding)
                    {
                        // Slow releases the remainder too. Those packets were going to be
                        // delivered; turning the module off is not a reason to destroy them.
                        await ReplayAsync().ConfigureAwait(false);
                    }

                    _wasHolding = holding;
                }

                var outbound = addr.Flags.HasFlag(WinDivertAddressFlag.Outbound);
                if (outbound)
                {
                    OutboundSeen++;
                }
                else
                {
                    InboundSeen++;
                }

                var verdict = Decide(packet, addr, out var peer);

                if (verdict == PacketVerdict.Pass)
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

                    if (verdict == PacketVerdict.Buffer && _buffer is not null)
                    {
                        _buffer.Hold(packet, addr, DateTimeOffset.UtcNow, peer);
                        PacketsBuffered++;
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
    /// Sends everything the buffer still considers worth replaying, oldest first.
    /// </summary>
    /// <remarks>
    /// Sent as fast as they will go. The reference limiter replays in millisecond-spaced bursts
    /// and the game copes, so there is nothing to gain by pacing - and pacing would mean the last
    /// actions of a hold arriving later still, which is the opposite of what they are held for.
    /// </remarks>
    private async Task ReplayAsync()
    {
        if (_buffer is null)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;

        foreach (var entry in _buffer.TakeForReplay(DateTimeOffset.UtcNow))
        {
            try
            {
                await _divert.SendAsync(entry.Packet, entry.Address).ConfigureAwait(false);
                PacketsReplayed++;
                sent++;
            }
            catch
            {
                // One packet that will not reinject is one packet lost, not a reason to abandon
                // the rest of the queue or to leave the remainder undisposed.
            }
            finally
            {
                entry.Dispose();
            }
        }

        Report(sent, started.Elapsed);
    }

    /// <summary>Records and announces what a drain cost.</summary>
    private void Report(int sent, TimeSpan took)
    {
        if (sent == 0)
        {
            return;
        }

        if (sent > LargestDrain)
        {
            LargestDrain = sent;
        }

        if (took > SlowestDrain)
        {
            SlowestDrain = took;
        }

        BufferDrained?.Invoke(sent, took);
    }

    /// <summary>
    /// Sends the held packets whose delay has now elapsed, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delay-line drain. Unlike a release it runs continuously, so the stream keeps flowing
    /// the whole time the module is on - just behind.
    /// </para>
    /// <para>
    /// It is driven by packet arrivals rather than a timer, because a timer would mean a second
    /// thread writing to the handle. On a port carrying traffic in both directions that is a fine
    /// clock: slow delays packets rather than blocking them, so the stream that drives the loop
    /// never stops. Where it is not - a port that has gone quiet in both directions - delivery
    /// waits for the next packet, and anything that passes <see cref="PacketBuffer.MaxAge"/> while
    /// waiting is discarded rather than sent stale.
    /// </para>
    /// </remarks>
    private async Task DrainDueAsync()
    {
        if (_buffer is null)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;

        foreach (var entry in _buffer.TakeDue(DateTimeOffset.UtcNow))
        {
            try
            {
                await _divert.SendAsync(entry.Packet, entry.Address).ConfigureAwait(false);
                PacketsReplayed++;
                sent++;
            }
            catch
            {
                // One packet that will not reinject is one packet lost, not a reason to abandon
                // the rest of the queue or to leave the remainder undisposed.
            }
            finally
            {
                entry.Dispose();
            }
        }

        Report(sent, started.Elapsed);
    }

    /// <summary>
    /// Asks the module about a packet, defaulting to passing it whenever anything goes wrong.
    /// </summary>
    /// <param name="packet">The packet just received.</param>
    /// <param name="addr">The address it arrived with.</param>
    /// <param name="peer">
    /// The remote address, handed back so a throttled buffer can pace per peer without describing
    /// the packet a second time. Null whenever the verdict came from a failure path.
    /// </param>
    private PacketVerdict Decide(
        WinDivertPacket packet, WinDivertAddress addr, out IPAddress? peer)
    {
        peer = null;

        try
        {
            var info = WinDivertPacketSource.Describe(packet, addr);
            if (info is null)
            {
                // Neither TCP nor UDP, or no IP header. It cannot belong to a port group, so it is
                // none of our business and must not be swallowed.
                PacketsPassedOnError++;
                return PacketVerdict.Pass;
            }

            peer = info.Value.RemoteAddress;
            var verdict = _module.Decide(info.Value);

            if (verdict == PacketVerdict.Pass && _module.IsActive)
            {
                PacketPassedWhileHolding?.Invoke(info.Value);
            }

            return verdict;
        }
        catch
        {
            // A module bug must not cost the user their connection. Passing is the safe default
            // even now that buffering exists: holding a packet we failed to understand risks
            // replaying something malformed into a live session.
            PacketsPassedOnError++;
            return PacketVerdict.Pass;
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

        // Held packets are discarded rather than flushed. Disposal is the kill switch, and it has
        // to restore normal traffic at once rather than first pushing a backlog of stale actions
        // into a session that may already be in trouble.
        _buffer?.Dispose();
        _divert.Dispose();
    }
}
