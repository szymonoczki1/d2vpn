using System.Runtime.Versioning;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Holds diverted packets so they can be sent when a hold is released.
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
/// usefulness: a position update thirty seconds stale is worse than useless on arrival, and
/// replaying it invites a correction for something that has long since moved on.
/// </para>
/// <para>
/// Eviction is oldest-first. What matters on release is the most recent actions - the shots just
/// taken - and the oldest entries are the ones whose moment has passed.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PacketBuffer : IDisposable
{
    private readonly Queue<Held> _held = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Most packets held before the oldest are discarded.</summary>
    public int Capacity { get; }

    /// <summary>Held packets older than this are discarded rather than replayed.</summary>
    public TimeSpan MaxAge { get; }

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

    /// <summary>Packets replayed, since construction.</summary>
    public long Replayed { get; private set; }

    /// <summary>Creates a buffer.</summary>
    public PacketBuffer(int capacity, TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero);

        Capacity = capacity;
        MaxAge = maxAge;
    }

    /// <summary>
    /// Takes a copy of a packet to replay later.
    /// </summary>
    /// <remarks>
    /// Clones rather than storing what was handed in: the receive loop reuses one packet and one
    /// address for every read, so keeping the originals would mean every entry pointing at the
    /// same buffer holding whatever arrived most recently.
    /// </remarks>
    public void Hold(WinDivertPacket packet, WinDivertAddress address, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_held.Count >= Capacity)
            {
                var evicted = _held.Dequeue();
                evicted.Dispose();
                Evicted++;
            }

            _held.Enqueue(new Held(packet.Clone(), address.Clone(), at));
        }
    }

    /// <summary>
    /// Hands back everything worth replaying, oldest first, and empties the buffer.
    /// </summary>
    /// <remarks>
    /// The caller owns what comes back and must dispose each entry after sending it. Returning
    /// them rather than sending them here keeps every write on the caller's thread, which is the
    /// receive loop - two threads writing to one WinDivert handle is not worth finding out about.
    /// </remarks>
    public IReadOnlyList<Held> TakeForReplay(DateTimeOffset now)
    {
        lock (_gate)
        {
            var replay = new List<Held>(_held.Count);

            while (_held.Count > 0)
            {
                var entry = _held.Dequeue();

                if (now - entry.HeldAt > MaxAge)
                {
                    entry.Dispose();
                    Expired++;
                    continue;
                }

                replay.Add(entry);
            }

            Replayed += replay.Count;
            return replay;
        }
    }

    /// <summary>Discards everything held without replaying it.</summary>
    public void DiscardAll()
    {
        lock (_gate)
        {
            while (_held.Count > 0)
            {
                _held.Dequeue().Dispose();
            }
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

            while (_held.Count > 0)
            {
                _held.Dequeue().Dispose();
            }
        }
    }

    /// <summary>One held packet and the address it arrived with.</summary>
    public sealed class Held(WinDivertPacket packet, WinDivertAddress address, DateTimeOffset heldAt)
        : IDisposable
    {
        /// <summary>The cloned packet.</summary>
        public WinDivertPacket Packet { get; } = packet;

        /// <summary>The cloned address, needed to reinject it the way it arrived.</summary>
        public WinDivertAddress Address { get; } = address;

        /// <summary>When it was taken.</summary>
        public DateTimeOffset HeldAt { get; } = heldAt;

        /// <inheritdoc/>
        public void Dispose()
        {
            Packet.Dispose();
            Address.Dispose();
        }
    }
}
