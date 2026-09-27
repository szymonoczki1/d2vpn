using System.Net;
using System.Runtime.Versioning;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Holds diverted packets so they can be sent later - on release, or once they come due.
/// </summary>
/// <remarks>
/// <para>
/// Every packet in here is a clone owning unmanaged memory, so this type is really about
/// lifetime. A packet leaves exactly once: replayed, evicted, expired, or disposed with the
/// buffer. Getting that wrong leaks for as long as the process runs, and the process is meant to
/// run for a whole play session.
/// </para>
/// <para>
/// Two limits, because they guard different things. <see cref="Capacity"/> bounds memory: a game
/// that stalls while held must not grow a queue without end. <see cref="MaxAge"/> bounds
/// usefulness: a position update thirty seconds stale is worse than useless on arrival. Measured
/// against a reference limiter, which uses 2048 and 35 s for those same two jobs, the values
/// chosen here independently agree to within 17% - worth more than any reasoning about them.
/// </para>
/// <para>
/// Eviction is oldest-first. What matters on release is the most recent actions - the shots just
/// taken - and the oldest entries are the ones whose moment has passed.
/// </para>
/// <para>
/// Each entry's <see cref="Held.DueAt"/> is computed when it is queued rather than when it is
/// inspected, which is what makes two quite different behaviours one mechanism. See
/// <see cref="SlowSchedule"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PacketBuffer : IDisposable
{
    // A linked list rather than a queue because a throttled schedule is not due in arrival order:
    // one peer can be far behind while another is current, so due entries come out of the middle.
    // Removal from anywhere is O(1) here, and iteration stays in arrival order.
    private readonly LinkedList<Held> _held = new();
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastQueuedAt = [];
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastDueAt = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Stands in for "no peer given", so everything paces as a single stream.</summary>
    private static readonly IPAddress AnyPeer = IPAddress.None;

    /// <summary>Most packets held before the oldest are discarded.</summary>
    public int Capacity { get; }

    /// <summary>Held packets older than this are discarded rather than sent.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>
    /// How held packets become due, or <see langword="null"/> to hold them until release.
    /// </summary>
    public SlowSchedule? Schedule { get; }

    /// <summary>Packets currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Packets discarded because the buffer was full, since construction.</summary>
    public long Evicted { get; private set; }

    /// <summary>Packets discarded for being too old, since construction.</summary>
    public long Expired { get; private set; }

    /// <summary>Packets sent, since construction.</summary>
    public long Replayed { get; private set; }

    /// <summary>Creates a buffer.</summary>
    /// <param name="capacity">Most packets to hold before evicting the oldest.</param>
    /// <param name="maxAge">Held packets older than this are discarded rather than sent.</param>
    /// <param name="schedule">
    /// How packets come due, or <see langword="null"/> to hold everything until release.
    /// </param>
    public PacketBuffer(int capacity, TimeSpan maxAge, SlowSchedule? schedule = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);

        Capacity = capacity;
        MaxAge = maxAge;
        Schedule = schedule;
    }

    /// <summary>
    /// Takes a copy of a packet to send later.
    /// </summary>
    /// <remarks>
    /// Clones rather than storing what was handed in: the receive loop reuses one packet and one
    /// address for every read, so keeping the originals would mean every entry pointing at the
    /// same buffer holding whatever arrived most recently.
    /// </remarks>
    /// <param name="packet">The packet to copy.</param>
    /// <param name="address">The address it arrived with, needed to reinject it the same way.</param>
    /// <param name="at">When it arrived.</param>
    /// <param name="peer">
    /// The remote address, which a throttled schedule keys its pacing on. Omit to pace everything
    /// as a single stream.
    /// </param>
    public void Hold(
        WinDivertPacket packet, WinDivertAddress address, DateTimeOffset at, IPAddress? peer = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_held.Count >= Capacity)
            {
                var evicted = _held.First!.Value;
                _held.RemoveFirst();
                evicted.Dispose();
                Evicted++;
            }

            var due = DueFor(peer ?? AnyPeer, at);
            _held.AddLast(new Held(packet.Clone(), address.Clone(), at, due));
        }
    }

    /// <summary>
    /// Decides when a packet queued now should go out. Callers hold the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole difference between the two slow behaviours is this one calculation.
    /// </para>
    /// <para>
    /// <b>Fixed</b> schedules from the packet's own arrival, so the output rate equals the input
    /// rate and the stream keeps its shape - it is simply shifted. A peer receiving it sees a
    /// complete, correctly shaped stream that is late, which is indistinguishable from latency and
    /// gets compensated for accordingly.
    /// </para>
    /// <para>
    /// <b>Throttled</b> schedules from the last already-queued packet for the same peer, so the
    /// output rate is one packet per delay whatever the input rate. Input outruns output, the
    /// backlog grows, and the effective delay climbs for as long as the module is held. The peer is
    /// not receiving late data, it is receiving a starved stream. This is a reference limiter's
    /// behaviour, read from its source.
    /// </para>
    /// <para>
    /// The burst window keeps a burst a burst. Packets arriving within it of the previous one are
    /// spaced by the window rather than by the full delay, so a four-packet burst is not smeared
    /// across four seconds - which would throttle far harder than one-per-delay suggests.
    /// </para>
    /// </remarks>
    private DateTimeOffset DueFor(IPAddress peer, DateTimeOffset at)
    {
        if (Schedule is not { } schedule)
        {
            // Held until release; nothing ever comes due on its own.
            return DateTimeOffset.MaxValue;
        }

        if (!schedule.Throttle)
        {
            return at + schedule.Delay;
        }

        var step = _lastQueuedAt.TryGetValue(peer, out var lastQueued)
            && at - lastQueued < schedule.EffectiveBurstWindow
                ? schedule.EffectiveBurstWindow
                : schedule.Delay;

        // Chain from the last scheduled release only while it is still ahead of now. Once this
        // peer's queue has drained the chain restarts from arrival, so a peer is not penalised for
        // a backlog it no longer has.
        var basis = _lastDueAt.TryGetValue(peer, out var lastDue) && lastDue > at ? lastDue : at;

        var due = basis + step;
        _lastQueuedAt[peer] = at;
        _lastDueAt[peer] = due;
        return due;
    }

    /// <summary>
    /// Hands back the packets that are now due, in arrival order, leaving the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delay-line drain, as against <see cref="TakeForReplay"/>'s release-everything drain. A
    /// slow module does not block: every packet still arrives, later than it should have, which
    /// desynchronises without the peer ever seeing a gap in the stream.
    /// </para>
    /// <para>
    /// Order is preserved per peer, which is what matters - delivering a stream late is the point,
    /// delivering it shuffled is just corruption. It is not preserved <em>across</em> peers, since
    /// those are separate conversations and the relative order of two of them carries no meaning.
    /// A reference limiter reaches the same conclusion, flushing per-peer groups in parallel and
    /// ordering only within each.
    /// </para>
    /// <para>
    /// As with <see cref="TakeForReplay"/> the caller owns what comes back and must dispose each
    /// entry, so that every write to the handle stays on one thread.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Held> TakeDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<Held>? due = null;
            var node = _held.First;

            while (node is not null)
            {
                var next = node.Next;
                var entry = node.Value;

                // Age still applies. If delivery stalls long enough that a packet passes MaxAge
                // while queued, it is past being worth sending and replaying it does harm. Under a
                // throttle this is reachable by design rather than only by a stall: a backlog that
                // grows faster than it drains will age its own tail out.
                if (now - entry.HeldAt > MaxAge)
                {
                    _held.Remove(node);
                    entry.Dispose();
                    Expired++;
                }
                else if (entry.DueAt <= now)
                {
                    _held.Remove(node);
                    (due ??= []).Add(entry);
                }

                node = next;
            }

            if (due is null)
            {
                return [];
            }

            Replayed += due.Count;
            return due;
        }
    }

    /// <summary>
    /// Hands back everything worth sending, in arrival order, and empties the buffer.
    /// </summary>
    /// <remarks>
    /// The release drain: on untick the whole buffer goes, due or not. The caller owns what comes
    /// back and must dispose each entry.
    /// </remarks>
    public IReadOnlyList<Held> TakeForReplay(DateTimeOffset now)
    {
        lock (_gate)
        {
            var replay = new List<Held>(_held.Count);

            while (_held.Count > 0)
            {
                var entry = _held.First!.Value;
                _held.RemoveFirst();

                if (now - entry.HeldAt > MaxAge)
                {
                    entry.Dispose();
                    Expired++;
                    continue;
                }

                replay.Add(entry);
            }

            // The schedule restarts with the next hold. Carrying a drained peer's chain across a
            // release would have it scheduling against a backlog that no longer exists.
            _lastQueuedAt.Clear();
            _lastDueAt.Clear();

            Replayed += replay.Count;
            return replay;
        }
    }

    /// <summary>Discards everything held without sending it.</summary>
    public void DiscardAll()
    {
        lock (_gate)
        {
            Drop();
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
            Drop();
        }
    }

    /// <summary>Frees everything held. Callers hold the lock.</summary>
    private void Drop()
    {
        while (_held.Count > 0)
        {
            var entry = _held.First!.Value;
            _held.RemoveFirst();
            entry.Dispose();
        }

        _lastQueuedAt.Clear();
        _lastDueAt.Clear();
    }

    /// <summary>How a slow module decides when a held packet goes out.</summary>
    /// <param name="Delay">How long a packet waits.</param>
    /// <param name="Throttle">
    /// <see langword="false"/> to schedule from each packet's own arrival, so the stream keeps its
    /// shape and is shifted. <see langword="true"/> to schedule from the last already-queued packet
    /// for the same peer, capping the output rate and letting the backlog grow.
    /// </param>
    /// <param name="BurstWindow">
    /// Packets arriving within this of the previous one are spaced by it rather than by
    /// <paramref name="Delay"/>, so a burst stays a burst. Throttled schedules only; defaults to
    /// 10 ms, which is a reference limiter's value.
    /// </param>
    public sealed record SlowSchedule(
        TimeSpan Delay, bool Throttle = false, TimeSpan BurstWindow = default)
    {
        /// <summary>The burst window, defaulted if it was left unset.</summary>
        public TimeSpan EffectiveBurstWindow =>
            BurstWindow > TimeSpan.Zero ? BurstWindow : TimeSpan.FromMilliseconds(10);
    }

    /// <summary>One held packet and the address it arrived with.</summary>
    public sealed class Held(
        WinDivertPacket packet,
        WinDivertAddress address,
        DateTimeOffset heldAt,
        DateTimeOffset dueAt)
        : IDisposable
    {
        /// <summary>The cloned packet.</summary>
        public WinDivertPacket Packet { get; } = packet;

        /// <summary>The cloned address, needed to reinject it the way it arrived.</summary>
        public WinDivertAddress Address { get; } = address;

        /// <summary>When it was taken.</summary>
        public DateTimeOffset HeldAt { get; } = heldAt;

        /// <summary>When it should go out, decided when it was queued.</summary>
        public DateTimeOffset DueAt { get; } = dueAt;

        /// <inheritdoc/>
        public void Dispose()
        {
            Packet.Dispose();
            Address.Dispose();
        }
    }
}
