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
    private readonly IReadOnlyList<ModuleSlot> _slots;
    private readonly TcpAckRewriter? _rewriter;
    private readonly ConnectionSampler? _sampler;
    private int _resetRequested;
    private bool _disposed;

    /// <summary>The slots sharing this handle, in the order they are consulted.</summary>
    public IReadOnlyList<ModuleSlot> Slots => _slots;

    /// <summary>
    /// The module deciding each packet, for a handle carrying exactly one.
    /// </summary>
    /// <exception cref="InvalidOperationException">This handle carries several.</exception>
    public ITrafficModule Module => _slots.Count == 1
        ? _slots[0].Module
        : throw new InvalidOperationException(
            $"this handle carries {_slots.Count} modules - use Slots");

    /// <summary>True while any slot is holding.</summary>
    public bool AnyHolding => _slots.Any(s => s.Module.IsActive);

    /// <summary>The port group this handle matches.</summary>
    /// <remarks>
    /// Every module on one handle acts on the same group - the constructor refuses a set that does
    /// not - so the handle has exactly one, and a watcher can pick out the group it cares about.
    /// </remarks>
    public PortGroup Group { get; }

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

    /// <summary>Resets injected since the controller started.</summary>
    public long ResetsInjected { get; private set; }

    /// <summary>Held packets thrown away because the connection they belonged to was reset.</summary>
    /// <remarks>
    /// Expected rather than alarming when a hold and a reconnect are used together: everything
    /// queued at the moment of a reset belonged to the connection being killed.
    /// </remarks>
    public long DiscardedOnReset { get; private set; }

    /// <summary>Connections reset by the most recent request.</summary>
    public int LastResetConnections { get; private set; }

    /// <summary>Raised after a reset request is carried out, with how many connections it hit.</summary>
    public event Action<int, int>? ResetsSent;

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
    /// Raised for every packet on the group, before any module has had a verdict on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For something that only wants to watch - the instance timer is the case. Before the verdict
    /// on purpose: a watcher that saw only what was passed would go blind for exactly as long as a
    /// hold lasted, and a watcher whose rule is "silence means it ended" would then end the
    /// instance in the middle of one.
    /// </para>
    /// <para>
    /// It has to come from in here rather than from a second handle. A filter is built from the
    /// port group alone, so another handle on the same group sits behind this one by priority and
    /// sees only what this one chose to reinject. There is no vantage point outside the handle from
    /// which a withheld packet is visible.
    /// </para>
    /// <para>
    /// Raised on the receive loop. A handler must be quick and must not touch a UI directly.
    /// </para>
    /// </remarks>
    public event Action<PacketInfo>? PacketObserved;

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
    /// <param name="rewriter">
    /// Brings a held TCP segment's acknowledgement number up to date before it is replayed, or
    /// <see langword="null"/> for UDP, where a packet replays exactly as captured.
    /// </param>
    /// <param name="sampler">
    /// Keeps a recent packet per connection so resets can be built from them, or
    /// <see langword="null"/> for a controller that never injects.
    /// </param>
    public WinDivertTrafficController(
        ITrafficModule module,
        short priority = 0,
        PacketBuffer? buffer = null,
        TcpAckRewriter? rewriter = null,
        ConnectionSampler? sampler = null)
        : this([new ModuleSlot(module, buffer)], priority, rewriter, sampler)
    {
    }

    /// <summary>
    /// Opens one diverting handle shared by several modules on the same port group.
    /// </summary>
    /// <remarks>
    /// Several modules on one group cannot each have a handle: a filter is built from the group
    /// alone, so they would all match the same packets and the lower-priority ones would see only
    /// what the first reinjected. See <see cref="ModuleSlot"/>.
    /// </remarks>
    /// <param name="slots">
    /// The modules sharing this handle, consulted in order. The first non-passing verdict wins and
    /// that slot's buffer keeps the packet.
    /// </param>
    /// <param name="priority">Handle priority.</param>
    /// <param name="rewriter">Shared ack rewriter, or null for UDP.</param>
    /// <param name="sampler">Shared sampler, or null if this handle never injects.</param>
    /// <exception cref="ArgumentException">
    /// No slots, or slots on different port groups - which could not share a filter.
    /// </exception>
    public WinDivertTrafficController(
        IReadOnlyList<ModuleSlot> slots,
        short priority = 0,
        TcpAckRewriter? rewriter = null,
        ConnectionSampler? sampler = null)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            throw new ArgumentException("a handle needs at least one module", nameof(slots));
        }

        var group = slots[0].Module.Group;

        if (slots.Any(slot => slot.Module.Group != group))
        {
            throw new ArgumentException(
                "every module on one handle must act on the same port group, because the filter is "
                + "built from the group and nothing else",
                nameof(slots));
        }

        _slots = slots;
        _rewriter = rewriter;
        _sampler = sampler;
        Group = group;

        var filter = WinDivertFilterBuilder.Build(group);
        FilterExpression = filter.ToString() ?? string.Empty;

        try
        {
            // No Sniff flag: this genuinely diverts. A packet not reinjected does not arrive.
            _divert = new WinDivert(filter, WinDivertLayer.Network, priority, WinDivertFlag.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not open WinDivert for {group} in divert mode. This needs an "
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

        // Declared outside the try so the exit path can wait for it. A read left outstanding
        // when the handle closes registers a cancellation callback against that handle, and the
        // callback runs after disposal - which threw ObjectDisposedException out of the Ctrl+C
        // handler. Before the loop kept a read across iterations this could not happen, because
        // every read was awaited before the loop could exit.
        Task? receive = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                receive ??= _divert.RecvAsync(packet, addr, cancellationToken).AsTask();

                // A tick alongside the read, so a drain does not have to wait for the next packet.
                // Draining stays on this thread - a hold is released from a hotkey, and every write
                // to the handle belongs here - but on a quiet port "this thread, next packet" can
                // be most of a second away. Measured on 7500 at 2.3 packets/s, which is a ~430 ms
                // average gap and was felt in game as a delay after unticking. The read is not
                // cancelled to do this; it is simply left outstanding until it completes.
                if (_sampler is not null || _slots.Any(slot => slot.Buffer is not null))
                {
                    var tick = Task.Delay(DrainInterval, cancellationToken);
                    var finished = await Task.WhenAny(receive, tick).ConfigureAwait(false);

                    if (!ReferenceEquals(finished, receive))
                    {
                        // Cancellation also completes the tick, and shutdown is not a moment to
                        // push a backlog into a session: disposal discards the buffer instead.
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await DrainAsync().ConfigureAwait(false);
                        await InjectResetsAsync().ConfigureAwait(false);
                        continue;
                    }
                }

                try
                {
                    await receive.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    receive = null;
                }

                PacketsSeen++;

                // Before this packet is sent, so held actions arrive ahead of the live one that
                // follows them rather than behind it.
                await DrainAsync().ConfigureAwait(false);
                await InjectResetsAsync().ConfigureAwait(false);

                var outbound = addr.Flags.HasFlag(WinDivertAddressFlag.Outbound);
                if (outbound)
                {
                    OutboundSeen++;
                }
                else
                {
                    InboundSeen++;
                }

                var verdict = Decide(packet, addr, out var peer, out var owner);

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

                    owner?.CountDropped();

                    if (verdict == PacketVerdict.Buffer && owner?.Buffer is { } slotBuffer)
                    {
                        slotBuffer.Hold(packet, addr, DateTimeOffset.UtcNow, peer);
                        PacketsBuffered++;
                        owner.CountBuffered();
                    }
                }
            }
        }
        finally
        {
            // Let the outstanding read finish before the handle goes. It is already cancelled by
            // this point, so this is waiting for the cancellation to be observed rather than for a
            // packet - and whatever it throws is the reason we are here anyway.
            if (receive is not null)
            {
                try
                {
                    await receive.ConfigureAwait(false);
                }
                catch
                {
                    // Cancelled, or the handle failed. Either way there is nothing to salvage and
                    // the close below is what matters.
                }
            }

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
    private async Task ReplayAsync(ModuleSlot slot, PacketBuffer buffer)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;

        foreach (var entry in buffer.TakeForReplay(DateTimeOffset.UtcNow))
        {
            try
            {
                _rewriter?.Rewrite(entry.Packet, entry.Address);
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

        slot.CountReplayed(sent);
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
    /// How often the loop wakes to drain when no packet has arrived.
    /// </summary>
    /// <remarks>
    /// Sets the worst-case delay between a release and the held traffic going out. Small enough to
    /// be imperceptible against a hotkey, and cheap: an idle timer a hundred times a second costs
    /// nothing next to a game.
    /// </remarks>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Asks for every live connection on this handle's group to be reset.
    /// </summary>
    /// <remarks>
    /// Safe to call from any thread. The work happens on the receive loop, within one drain
    /// interval, because every write to the handle belongs there - this is triggered from a hotkey.
    /// </remarks>
    public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);

    /// <summary>Builds and sends resets, if one was asked for.</summary>
    private async Task InjectResetsAsync()
    {
        if (_sampler is null || Interlocked.Exchange(ref _resetRequested, 0) == 0)
        {
            return;
        }

        var resets = _sampler.BuildResets(DateTimeOffset.UtcNow);
        var connections = 0;
        var sent = 0;
        ConnectionSampler.Connection? previous = null;

        foreach (var reset in resets)
        {
            try
            {
                if (previous != reset.Connection)
                {
                    connections++;
                    previous = reset.Connection;
                }

                await _divert.SendAsync(reset.Packet, reset.Address).ConfigureAwait(false);
                ResetsInjected++;
                sent++;
            }
            catch
            {
                // One connection not killed is not a reason to leave the others alive, or to
                // leave the remaining resets undisposed.
            }
            finally
            {
                reset.Dispose();
            }
        }

        // Whatever was held belonged to the connections just killed. Their sequence numbers
        // mean nothing now, so delivering them would inject packets for a connection that no
        // longer exists - which the stack answers with resets of its own. Discarded rather than
        // replayed, for the same reason disposal discards rather than flushing.
        if (sent > 0)
        {
            foreach (var buffer in _slots.Select(slot => slot.Buffer).OfType<PacketBuffer>())
            {
                DiscardedOnReset += buffer.Count;
                buffer.DiscardAll();
            }
        }

        LastResetConnections = connections;
        ResetsSent?.Invoke(connections, sent);
    }

    /// <summary>
    /// Sends whatever the buffer now owes: due packets under a schedule, everything on a release.
    /// </summary>
    private async Task DrainAsync()
    {
        // Per slot, not per handle. With one release flag for the whole handle, turning off an
        // uplink hold would have replayed the inbound packets a downlink hold was still holding.
        foreach (var slot in _slots)
        {
            if (slot.Buffer is not { } buffer)
            {
                continue;
            }

            if (buffer.Schedule is not null)
            {
                await DrainDueAsync(slot, buffer).ConfigureAwait(false);
            }

            if (slot.TakeReleaseEdge())
            {
                // A release sends the remainder too, due or not. Those packets were going to be
                // delivered; turning the module off is not a reason to destroy them.
                await ReplayAsync(slot, buffer).ConfigureAwait(false);
            }
        }
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
    private async Task DrainDueAsync(ModuleSlot slot, PacketBuffer buffer)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;

        foreach (var entry in buffer.TakeDue(DateTimeOffset.UtcNow))
        {
            try
            {
                _rewriter?.Rewrite(entry.Packet, entry.Address);
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

        slot.CountReplayed(sent);
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
    /// <param name="owner">
    /// The slot that withheld the packet, so its own buffer keeps it. Null when the packet passed.
    /// </param>
    private PacketVerdict Decide(
        WinDivertPacket packet,
        WinDivertAddress addr,
        out IPAddress? peer,
        out ModuleSlot? owner)
    {
        peer = null;
        owner = null;

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

            // Every packet, held or not: the replay needs a current value, and the only way to
            // have one is to have been watching all along.
            _rewriter?.Observe(info.Value);
            _sampler?.Observe(info.Value, packet, addr);

            // Wrapped separately from the verdict below rather than sharing the outer catch. That
            // one passes the packet, which is the right answer to a module bug, but it would also
            // skip every module's verdict because something that merely watches had thrown.
            if (PacketObserved is { } observer)
            {
                try
                {
                    observer(info.Value);
                }
                catch
                {
                    // Nothing that only watches is worth a dropped hold.
                }
            }

            // First non-passing verdict wins, in declared order. A packet can only be handled
            // once, and the slot that withheld it has to be the one whose buffer keeps it -
            // otherwise releasing one module would replay another's backlog.
            foreach (var slot in _slots)
            {
                var slotVerdict = slot.Module.Decide(info.Value);

                if (slotVerdict != PacketVerdict.Pass)
                {
                    owner = slot;
                    return slotVerdict;
                }
            }

            if (AnyHolding)
            {
                PacketPassedWhileHolding?.Invoke(info.Value);
            }

            return PacketVerdict.Pass;
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

        foreach (var slot in _slots)
        {
            slot.Module.Reset();
        }

        // Held packets are discarded rather than flushed. Disposal is the kill switch, and it has
        // to restore normal traffic at once rather than first pushing a backlog of stale actions
        // into a session that may already be in trouble. Each slot disposes its own buffer.
        foreach (var slot in _slots)
        {
            slot.Dispose();
        }

        _sampler?.Dispose();
        _divert.Dispose();
    }
}
